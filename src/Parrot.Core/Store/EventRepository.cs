using Google.Protobuf;
using Microsoft.Data.Sqlite;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Store;

// The durable half of the event stream. The event and its projection commit in
// one transaction (principle 9), so a reader can never observe an event whose
// projection is missing, nor the reverse.
//
// EventBroker publishes only what this has already committed, which is what
// keeps a subscriber from seeing an event a crash would un-happen.
//
// One connection carries all of it, and a SQLite connection holds one
// transaction at a time -- so the second writer is not slower, it is a
// corrupted connection. Admitting happens on whichever thread took the request
// and the drain writes on its own, so every method here takes the database gate.
internal sealed class EventRepository
{
    private const string UsageProjection = "agent-usage";
    private const long UsageProjectionVersion = 1;
    private const string ConversationProjection = "conversation";
    private const long ConversationProjectionVersion = 1;
    private const string ImageArtifactSelect =
        "SELECT artifact.artifact_id, content.sha256, content.media_type, content.byte_length, content.width, content.height, content.frame_count, content.aggregate_pixels, artifact.display_name, artifact.origin FROM image_artifact AS artifact JOIN image_content AS content ON content.sha256 = artifact.sha256";

    private readonly SessionDatabase _database;
    private readonly ImageArtifactStore? _imageStore;

    public EventRepository(SessionDatabase database) => _database = database;

    public EventRepository(SessionDatabase database, ImageArtifactStore imageStore)
    {
        _database = database;
        _imageStore = imageStore;
    }

    public SessionUsage? Append(Event published, string? messageRole, string? messageContent) =>
        Append(
            published,
            messageRole is null || messageContent is null ? null : Message(messageRole, messageContent),
            messageRole is null ? ConversationOrigin.Model : Origin(messageRole));

    public SessionUsage? Append(Event published, LLMMessage? message, ConversationOrigin origin)
    {
        ArgumentNullException.ThrowIfNull(published);

        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            if (published.PayloadCase == Event.PayloadOneofCase.AgentStatisticsUpdated)
            {
                EnsureUsageProjection(transaction);
            }

            var revision = Record(transaction, published);

            if (message is not null)
            {
                Project(transaction, published.AgentSessionId, message, origin);
            }

            SessionUsage? usage = null;
            if (published.PayloadCase == Event.PayloadOneofCase.AgentStatisticsUpdated)
            {
                ProjectUsage(transaction, published.AgentSessionId, revision, published.AgentStatisticsUpdated);
                usage = CaptureUsage(transaction);
            }

            transaction.Commit();
            return usage;
        }
    }

    public void AppendConversation(
        Event published,
        ConversationOrigin origin,
        LLMRole role,
        IReadOnlyList<ConversationPart> parts,
        IReadOnlyList<LLMToolCall> toolCalls,
        string toolCallId)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(toolCalls);
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            _ = Record(transaction, published);
            _ = Project(transaction, published.AgentSessionId, origin, role, parts, toolCalls, toolCallId);
            transaction.Commit();
        }
    }

    public bool AppendToolResult(
        Event published,
        long assistantSequence,
        ToolExecutionTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(terminal);
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            if (HasToolResult(transaction, published.AgentSessionId, assistantSequence, terminal.ToolCallId))
            {
                transaction.Commit();
                return false;
            }

            _ = Record(transaction, published);
            InsertToolResult(transaction, published.AgentSessionId, assistantSequence, terminal);
            transaction.Commit();
            return true;
        }
    }

    public bool HasToolSynthetic(long assistantSequence, string agentSessionId)
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            var exists = HasToolSynthetic(transaction, agentSessionId, assistantSequence);
            transaction.Commit();
            return exists;
        }
    }

    public bool AppendToolSynthetic(
        Event published,
        long assistantSequence,
        IReadOnlyList<ConversationPart> imageParts)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(imageParts);
        if (imageParts.Count == 0 || imageParts.Any(part => part.Kind != ConversationPartKind.ImageArtifact))
        {
            throw new ArgumentException("A tool synthetic message requires image artifact parts.", nameof(imageParts));
        }

        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            if (HasToolSynthetic(transaction, published.AgentSessionId, assistantSequence))
            {
                transaction.Commit();
                return false;
            }

            _ = Record(transaction, published);
            var sequence = Project(
                transaction,
                published.AgentSessionId,
                ConversationOrigin.Tool,
                LLMRole.User,
                imageParts,
                [],
                string.Empty);
            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO tool_batch_synthetic (agent_session, assistant_sequence, item_sequence) "
                + "VALUES ($session, $assistant, $item);";
            _ = insert.Parameters.AddWithValue("$session", published.AgentSessionId);
            _ = insert.Parameters.AddWithValue("$assistant", assistantSequence);
            _ = insert.Parameters.AddWithValue("$item", sequence);
            _ = insert.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
    }

    // Accepts a prompt without promoting it: it becomes durable here, and joins
    // the conversation only when the drain reaches the boundary its delivery
    // asks for. That order is principle 1 -- a prompt is durable before
    // execution is requested -- and it is the whole reason a queued prompt
    // survives the process that took it.
    //
    // Idempotent on the sender's message id, so a re-send after a dropped
    // connection admits nothing the second time. The same id carrying different
    // text is refused rather than silently resolved either way.
    public Admission Admit(
        string agentSessionId,
        string messageId,
        string content,
        Delivery delivery,
        Func<AdmittedInput, Event> compose) =>
        Admit(agentSessionId, messageId, [ConversationPart.TextPart(content)], delivery, compose);

    public Admission Admit(
        string agentSessionId,
        string messageId,
        IReadOnlyList<ConversationPart> parts,
        Delivery delivery,
        Func<AdmittedInput, Event> compose)
    {
        ArgumentNullException.ThrowIfNull(parts);
        ArgumentNullException.ThrowIfNull(compose);

        lock (_database.Gate)
        {
            using var transaction = _database.Begin();

            if (Existing(transaction, agentSessionId, messageId) is { } already)
            {
                return already.Parts.SequenceEqual(parts) && already.Delivery == delivery
                    ? new Admission(already, null)
                    : throw new InputConflictException(
                        $"message {messageId} was already admitted with different content");
            }

            var admitted = new AdmittedInput(Identifier.InputId(), messageId, parts, delivery);
            var published = compose(admitted);

            using (var insert = _database.Connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO input (id, agent_session, message_id, content, delivery, status, created_at)
                    VALUES ($id, $session, $message, $content, $delivery, 'pending', $at);
                    """;
                _ = insert.Parameters.AddWithValue("$id", admitted.Id);
                _ = insert.Parameters.AddWithValue("$session", agentSessionId);
                _ = insert.Parameters.AddWithValue("$message", messageId);
                _ = insert.Parameters.AddWithValue("$content", admitted.Content);
                _ = insert.Parameters.AddWithValue("$delivery", Text(delivery));
                _ = insert.Parameters.AddWithValue("$at", Timestamp());
                _ = insert.ExecuteNonQuery();
            }

            InsertInputParts(transaction, admitted.Id, admitted.Parts);
            _ = Record(transaction, published);
            transaction.Commit();

            return new Admission(admitted, published);
        }
    }

    public Admission? AdmitSteerIfIdle(
        string agentSessionId,
        string messageId,
        string content,
        Func<AdmittedInput, Event> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);

        lock (_database.Gate)
        {
            using var transaction = _database.Begin();

            if (Existing(transaction, agentSessionId, messageId) is { } already)
            {
                return already.Content == content && already.Delivery == Delivery.Steer
                    ? new Admission(already, null)
                    : throw new InputConflictException(
                        $"message {messageId} was already admitted with different content");
            }

            using (var pending = _database.Connection.CreateCommand())
            {
                pending.Transaction = transaction;
                pending.CommandText =
                    "SELECT EXISTS (SELECT 1 FROM input WHERE agent_session = $session AND status = 'pending');";
                _ = pending.Parameters.AddWithValue("$session", agentSessionId);

                if (Convert.ToInt64(
                    pending.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
                {
                    transaction.Commit();
                    return null;
                }
            }

            var admitted = new AdmittedInput(Identifier.InputId(), messageId, content, Delivery.Steer);
            var published = compose(admitted);

            using (var insert = _database.Connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO input (id, agent_session, message_id, content, delivery, status, created_at)
                    VALUES ($id, $session, $message, $content, 'steer', 'pending', $at);
                    """;
                _ = insert.Parameters.AddWithValue("$id", admitted.Id);
                _ = insert.Parameters.AddWithValue("$session", agentSessionId);
                _ = insert.Parameters.AddWithValue("$message", messageId);
                _ = insert.Parameters.AddWithValue("$content", content);
                _ = insert.Parameters.AddWithValue("$at", Timestamp());
                _ = insert.ExecuteNonQuery();
            }

            _ = Record(transaction, published);
            transaction.Commit();
            return new Admission(admitted, published);
        }
    }

    // Every pending steer, oldest first. Upstream bounds this by a sequence
    // cutoff; here the single transaction is the boundary, so a steer admitted
    // while this runs simply lands at the next one.
    public IReadOnlyList<Promotion> PromoteSteers(string agentSessionId, Func<AdmittedInput, Event> compose) =>
        Promote(agentSessionId, Delivery.Steer, -1, compose);

    // At most one. A queued prompt is a turn of its own, so promoting the whole
    // queue at once would run them together as if they had been sent together.
    public IReadOnlyList<Promotion> PromoteNextQueue(string agentSessionId, Func<AdmittedInput, Event> compose) =>
        Promote(agentSessionId, Delivery.Queue, 1, compose);

    // Whether anything is admitted and still waiting. The interrupt path asks,
    // so that stopping a turn does not also discard what was queued behind it.
    public bool HasPendingInputs(string agentSessionId)
    {
        lock (_database.Gate)
        {
            using var read = _database.Connection.CreateCommand();
            read.CommandText =
                "SELECT EXISTS (SELECT 1 FROM input WHERE agent_session = $session AND status = 'pending');";
            _ = read.Parameters.AddWithValue("$session", agentSessionId);

            return Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
    }

    public IReadOnlyList<TodoItem> ReadTodos(string agentSessionId, CancellationToken cancellationToken)
    {
        var items = new List<TodoItem>();

        lock (_database.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var read = _database.Connection.CreateCommand();
            read.CommandText =
                "SELECT id, content, status, priority, position FROM todo "
                + "WHERE agent_session = $session ORDER BY position;";
            _ = read.Parameters.AddWithValue("$session", agentSessionId);

            using var reader = read.ExecuteReader();

            while (reader.Read())
            {
                cancellationToken.ThrowIfCancellationRequested();
                items.Add(new TodoItem(
                    (string)reader["id"],
                    (string)reader["content"],
                    ParseTodoStatus((string)reader["status"]),
                    ParseTodoPriority((string)reader["priority"]),
                    Convert.ToInt32(reader["position"], System.Globalization.CultureInfo.InvariantCulture)));
            }
        }

        return items;
    }

    public void ReplaceTodos(
        string agentSessionId,
        IReadOnlyList<TodoItem> items,
        Event published,
        CancellationToken cancellationToken)
    {
        lock (_database.Gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = _database.Begin();
            using (var delete = _database.Connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM todo WHERE agent_session = $session;";
                _ = delete.Parameters.AddWithValue("$session", agentSessionId);
                _ = delete.ExecuteNonQuery();
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var insert = _database.Connection.CreateCommand();
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO todo (agent_session, id, position, content, status, priority)
                    VALUES ($session, $id, $position, $content, $status, $priority);
                    """;
                _ = insert.Parameters.AddWithValue("$session", agentSessionId);
                _ = insert.Parameters.AddWithValue("$id", item.Id);
                _ = insert.Parameters.AddWithValue("$position", item.Position);
                _ = insert.Parameters.AddWithValue("$content", item.Content);
                _ = insert.Parameters.AddWithValue("$status", TodoStatusText(item.Status));
                _ = insert.Parameters.AddWithValue("$priority", TodoPriorityText(item.Priority));
                _ = insert.ExecuteNonQuery();
            }

            _ = Record(transaction, published);
            transaction.Commit();
        }
    }

    public IReadOnlyList<Event> Replay()
    {
        var events = new List<Event>();

        lock (_database.Gate)
        {
            using var read = _database.Connection.CreateCommand();
            read.CommandText = "SELECT payload FROM event ORDER BY sequence;";

            using var reader = read.ExecuteReader();

            while (reader.Read())
            {
                events.Add(Event.Parser.ParseFrom((byte[])reader["payload"]));
            }
        }

        return events;
    }

    public SessionUsage Usage()
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            EnsureUsageProjection(transaction);
            var usage = CaptureUsage(transaction);
            transaction.Commit();
            return usage;
        }
    }

    public AgentStatistics? LatestStatistics(string agentSessionId)
    {
        lock (_database.Gate)
        {
            using var read = _database.Connection.CreateCommand();
            read.CommandText =
                "SELECT payload FROM event WHERE agent_session = $session ORDER BY sequence DESC;";
            _ = read.Parameters.AddWithValue("$session", agentSessionId);

            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var published = Event.Parser.ParseFrom((byte[])reader["payload"]);
                if (published.PayloadCase == Event.PayloadOneofCase.AgentStatisticsUpdated)
                {
                    return AgentStatistics.Restore(published.AgentStatisticsUpdated);
                }
            }

            return null;
        }
    }

    public IReadOnlyList<string> Messages(string agentSessionId) =>
        [.. ModelHistory(agentSessionId).Select(message => $"{Text(message.Role)}: {message.Content}")];

    public IReadOnlyList<LLMMessage> ModelHistory(string agentSessionId)
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            EnsureConversationProjection(transaction);
            var items = ReadConversation(transaction, agentSessionId, 0);
            transaction.Commit();
            return [.. items.Select(ToMessage)];
        }
    }

    public IReadOnlyList<LLMContent> Materialize(IReadOnlyList<ConversationPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        var contents = new List<LLMContent>(parts.Count);
        foreach (var part in parts)
        {
            if (part.Kind == ConversationPartKind.Text)
            {
                contents.Add(LLMContent.TextPart(part.Text));
                continue;
            }

            var store = _imageStore
                ?? throw new InvalidOperationException("image artifacts cannot be materialized without session resources");
            _ = ResolveImageArtifact(part.ArtifactId)
                ?? throw new FileNotFoundException("The image artifact does not exist.", part.ArtifactId);
            using var source = store.Open(part.ArtifactId);
            using var bytes = new MemoryStream();
            source.CopyTo(bytes);
            contents.Add(LLMContent.ImagePart(bytes.ToArray(), part.MediaType));
        }

        return contents;
    }

    public IReadOnlyList<ConversationItem> Conversation(string agentSessionId) =>
        ConversationAfter(agentSessionId, 0);

    public IReadOnlyList<ConversationItem> ConversationAfter(string agentSessionId, long watermark)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(watermark);
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            EnsureConversationProjection(transaction);
            var items = ReadConversation(transaction, agentSessionId, watermark);
            transaction.Commit();
            return items;
        }
    }

    public bool AppendToolTerminal(Event published, ToolExecutionTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(terminal);
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            var existing = ReadToolTerminal(transaction, published.AgentSessionId, terminal.ToolCallId);
            if (existing is not null)
            {
                if (!ToolTerminalsEqual(existing, terminal))
                {
                    throw new InputConflictException($"tool call {terminal.ToolCallId} was already settled differently");
                }

                transaction.Commit();
                return false;
            }

            _ = Record(transaction, published);
            InsertToolTerminal(transaction, published.AgentSessionId, terminal);
            transaction.Commit();
            return true;
        }
    }

    public void AppendToolSettlement(
        Event published,
        long assistantSequence,
        ToolExecutionTerminal terminal)
    {
        ArgumentNullException.ThrowIfNull(published);
        ArgumentNullException.ThrowIfNull(terminal);
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            var existing = ReadToolTerminal(transaction, published.AgentSessionId, terminal.ToolCallId);
            if (existing is not null && !ToolTerminalsEqual(existing, terminal))
            {
                throw new InputConflictException($"tool call {terminal.ToolCallId} was already settled differently");
            }

            if (existing is null)
            {
                _ = Record(transaction, published);
                InsertToolTerminal(transaction, published.AgentSessionId, terminal);
            }

            if (!HasToolResult(transaction, published.AgentSessionId, assistantSequence, terminal.ToolCallId))
            {
                InsertToolResult(transaction, published.AgentSessionId, assistantSequence, terminal);
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<ToolExecutionTerminal> ToolTerminals(string agentSessionId)
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            var terminals = new List<ToolExecutionTerminal>();
            using var read = _database.Connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText =
                "SELECT sequence, tool_call_id, tool_name, status, message FROM tool_execution_terminal "
                + "WHERE agent_session = $session ORDER BY sequence;";
            _ = read.Parameters.AddWithValue("$session", agentSessionId);
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var sequence = Convert.ToInt64(reader["sequence"], System.Globalization.CultureInfo.InvariantCulture);
                terminals.Add(new ToolExecutionTerminal(
                    (string)reader["tool_call_id"],
                    (string)reader["tool_name"],
                    ParseToolExecutionStatus((string)reader["status"]),
                    ReadToolResultParts(transaction, sequence),
                    (string)reader["message"]));
            }

            transaction.Commit();
            return terminals;
        }
    }

    public void SaveCompaction(string agentSessionId, CompactionSnapshot snapshot)
    {
        lock (_database.Gate)
        {
            using var command = _database.Connection.CreateCommand();
            command.CommandText =
                "INSERT INTO compaction_snapshot (agent_session, summary, watermark, created_at) "
                + "VALUES ($session, $summary, $watermark, $at) "
                + "ON CONFLICT (agent_session) DO UPDATE SET summary = excluded.summary, "
                + "watermark = excluded.watermark, created_at = excluded.created_at;";
            _ = command.Parameters.AddWithValue("$session", agentSessionId);
            _ = command.Parameters.AddWithValue("$summary", snapshot.Summary);
            _ = command.Parameters.AddWithValue("$watermark", snapshot.Watermark);
            _ = command.Parameters.AddWithValue("$at", Timestamp());
            _ = command.ExecuteNonQuery();
        }
    }

    public CompactionSnapshot? Compaction(string agentSessionId)
    {
        lock (_database.Gate)
        {
            using var command = _database.Connection.CreateCommand();
            command.CommandText =
                "SELECT summary, watermark FROM compaction_snapshot WHERE agent_session = $session;";
            _ = command.Parameters.AddWithValue("$session", agentSessionId);
            using var reader = command.ExecuteReader();
            return reader.Read()
                ? new CompactionSnapshot(
                    (string)reader["summary"],
                    Convert.ToInt64(reader["watermark"], System.Globalization.CultureInfo.InvariantCulture))
                : null;
        }
    }

    public (string AgentSessionId, string Mode) SessionState(string userSessionId, string requestedMode)
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR IGNORE INTO session_state (user_session, agent_session, mode) VALUES ($user, $agent, $mode);";
            _ = insert.Parameters.AddWithValue("$user", userSessionId);
            _ = insert.Parameters.AddWithValue("$agent", Identifier.AgentSession());
            _ = insert.Parameters.AddWithValue("$mode", requestedMode);
            _ = insert.ExecuteNonQuery();

            using var read = _database.Connection.CreateCommand();
            read.Transaction = transaction;
            read.CommandText = "SELECT agent_session, mode FROM session_state WHERE user_session = $user;";
            _ = read.Parameters.AddWithValue("$user", userSessionId);
            using var reader = read.ExecuteReader();
            _ = reader.Read();
            var state = ((string)reader["agent_session"], (string)reader["mode"]);
            transaction.Commit();
            return state;
        }
    }

    public void UpdateMode(string userSessionId, string agentSessionId, string mode)
    {
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            using var update = _database.Connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE session_state SET mode = $mode WHERE user_session = $user;";
            _ = update.Parameters.AddWithValue("$mode", mode);
            _ = update.Parameters.AddWithValue("$user", userSessionId);
            _ = update.ExecuteNonQuery();

            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO mode_change (agent_session, mode, created_at) VALUES ($session, $mode, $at);";
            _ = insert.Parameters.AddWithValue("$session", agentSessionId);
            _ = insert.Parameters.AddWithValue("$mode", mode);
            _ = insert.Parameters.AddWithValue("$at", Timestamp());
            _ = insert.ExecuteNonQuery();
            transaction.Commit();
        }
    }

    public bool StatusPromptPending(string agentSessionId) => PendingStatus(agentSessionId) is not null;

    public PendingStatus? PendingStatus(string agentSessionId)
    {
        lock (_database.Gate)
        {
            using var read = _database.Connection.CreateCommand();
            read.CommandText =
                """
                SELECT state.mode,
                       COALESCE((SELECT MAX(sequence) FROM mode_change WHERE agent_session = $session), -1) AS version,
                       COALESCE((SELECT MAX(mode_change_sequence) FROM status_prompt WHERE agent_session = $session), -2)
                           AS consumed
                FROM session_state AS state
                WHERE state.agent_session = $session;
                """;
            _ = read.Parameters.AddWithValue("$session", agentSessionId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            var version = Convert.ToInt64(reader["version"], System.Globalization.CultureInfo.InvariantCulture);
            var consumed = Convert.ToInt64(reader["consumed"], System.Globalization.CultureInfo.InvariantCulture);
            return version > consumed ? new PendingStatus((string)reader["mode"], version) : null;
        }
    }

    public bool AppendStatusPrompt(Event published, PendingStatus expected, string content)
    {
        ArgumentNullException.ThrowIfNull(published);

        published.StatusInjected = new StatusInjected();

        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            using var pending = _database.Connection.CreateCommand();
            pending.Transaction = transaction;
            pending.CommandText =
                """
                SELECT EXISTS (
                    SELECT 1 FROM session_state
                    WHERE agent_session = $session AND mode = $mode
                ) AND COALESCE((
                    SELECT MAX(sequence) FROM mode_change WHERE agent_session = $session
                ), -1) = $version
                AND $version > COALESCE((
                    SELECT MAX(mode_change_sequence) FROM status_prompt WHERE agent_session = $session
                ), -2);
                """;
            _ = pending.Parameters.AddWithValue("$session", published.AgentSessionId);
            _ = pending.Parameters.AddWithValue("$mode", expected.Mode);
            _ = pending.Parameters.AddWithValue("$version", expected.Version);

            if (Convert.ToInt64(
                pending.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                transaction.Commit();
                return false;
            }

            Project(transaction, published.AgentSessionId, "system", content);

            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                """
                INSERT INTO status_prompt (agent_session, mode_change_sequence, created_at)
                VALUES (
                    $session,
                    $version,
                    $at
                );
                """;
            _ = insert.Parameters.AddWithValue("$session", published.AgentSessionId);
            _ = insert.Parameters.AddWithValue("$version", expected.Version);
            _ = insert.Parameters.AddWithValue("$at", Timestamp());
            _ = insert.ExecuteNonQuery();
            transaction.Commit();
            return true;
        }
    }

    public void AppendInitialStatusPrompt(Event published, string content)
    {
        ArgumentNullException.ThrowIfNull(published);

        published.StatusInjected = new StatusInjected();

        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            Project(transaction, published.AgentSessionId, "system", content);
            transaction.Commit();
        }
    }

    public void AppendActiveWorkReminder(Event published, string content)
    {
        ArgumentNullException.ThrowIfNull(published);

        published.ActiveWorkReminderInjected = new ActiveWorkReminderInjected();

        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            Project(transaction, published.AgentSessionId, "system", content);
            transaction.Commit();
        }
    }

    public ImageArtifactMetadata RecordImageArtifact(ImageArtifactMetadata artifact, string uploadId)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        ArgumentException.ThrowIfNullOrWhiteSpace(uploadId);

        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            var existing = ImageUpload(transaction, uploadId);
            if (existing is not null)
            {
                if (existing == artifact)
                {
                    transaction.Commit();
                    return existing;
                }

                throw new InputConflictException($"image upload {uploadId} was already recorded with different content");
            }

            using (var content = _database.Connection.CreateCommand())
            {
                content.Transaction = transaction;
                content.CommandText =
                    """
                    INSERT INTO image_content (
                        sha256, media_type, byte_length, width, height, frame_count, aggregate_pixels)
                    VALUES ($sha256, $media_type, $byte_length, $width, $height, $frame_count, $aggregate_pixels)
                    ON CONFLICT(sha256) DO UPDATE SET
                        media_type = excluded.media_type,
                        byte_length = excluded.byte_length,
                        width = excluded.width,
                        height = excluded.height,
                        frame_count = excluded.frame_count,
                        aggregate_pixels = excluded.aggregate_pixels;
                    """;
                AddImageContent(content, artifact);
                _ = content.ExecuteNonQuery();
            }

            using (var insert = _database.Connection.CreateCommand())
            {
                insert.Transaction = transaction;
                insert.CommandText =
                    """
                    INSERT INTO image_artifact (artifact_id, sha256, display_name, origin, created_at)
                    VALUES ($artifact_id, $sha256, $display_name, $origin, $at)
                    ON CONFLICT(artifact_id) DO NOTHING;
                    """;
                _ = insert.Parameters.AddWithValue("$artifact_id", artifact.ArtifactId);
                _ = insert.Parameters.AddWithValue("$sha256", artifact.Sha256);
                _ = insert.Parameters.AddWithValue("$display_name", artifact.DisplayName);
                _ = insert.Parameters.AddWithValue("$origin", artifact.Origin);
                _ = insert.Parameters.AddWithValue("$at", Timestamp());
                _ = insert.ExecuteNonQuery();
            }

            var recorded = ResolveImageArtifact(transaction, artifact.ArtifactId)
                ?? throw new InvalidOperationException("The image artifact was not recorded.");
            if (recorded != artifact)
            {
                throw new InputConflictException($"image artifact {artifact.ArtifactId} was already recorded with different metadata");
            }

            using (var upload = _database.Connection.CreateCommand())
            {
                upload.Transaction = transaction;
                upload.CommandText =
                    "INSERT INTO image_upload (upload_id, artifact_id, created_at) VALUES ($upload_id, $artifact_id, $at);";
                _ = upload.Parameters.AddWithValue("$upload_id", uploadId);
                _ = upload.Parameters.AddWithValue("$artifact_id", artifact.ArtifactId);
                _ = upload.Parameters.AddWithValue("$at", Timestamp());
                _ = upload.ExecuteNonQuery();
            }

            transaction.Commit();
            return recorded;
        }
    }

    public ImageArtifactMetadata? ResolveImageArtifact(string artifactId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        lock (_database.Gate)
        {
            using var read = _database.Connection.CreateCommand();
            read.CommandText = ImageArtifactSelect + " WHERE artifact.artifact_id = $artifact_id;";
            _ = read.Parameters.AddWithValue("$artifact_id", artifactId);
            using var reader = read.ExecuteReader();
            if (!reader.Read())
            {
                return null;
            }

            return new ImageArtifactMetadata(
            (string)reader["artifact_id"],
            (string)reader["sha256"],
            (string)reader["media_type"],
            Convert.ToInt64(reader["byte_length"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["width"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["height"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["frame_count"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt64(reader["aggregate_pixels"], System.Globalization.CultureInfo.InvariantCulture),
            (string)reader["display_name"],
            (string)reader["origin"]);
        }
    }

    public void ClaimImageArtifact(string artifactId, string referenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            using var claim = _database.Connection.CreateCommand();
            claim.Transaction = transaction;
            claim.CommandText =
                "INSERT INTO image_artifact_reference (artifact_id, reference_id) SELECT artifact_id, $reference_id FROM image_artifact WHERE artifact_id = $artifact_id ON CONFLICT DO NOTHING;";
            _ = claim.Parameters.AddWithValue("$artifact_id", artifactId);
            _ = claim.Parameters.AddWithValue("$reference_id", referenceId);
            if (claim.ExecuteNonQuery() == 0 && ResolveImageArtifact(transaction, artifactId) is null)
            {
                throw new FileNotFoundException("The image artifact does not exist.", artifactId);
            }

            transaction.Commit();
        }
    }

    public void ReleaseImageArtifact(string artifactId, string referenceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(artifactId);
        ArgumentException.ThrowIfNullOrWhiteSpace(referenceId);
        lock (_database.Gate)
        {
            using var remove = _database.Connection.CreateCommand();
            remove.CommandText = "DELETE FROM image_artifact_reference WHERE artifact_id = $artifact_id AND reference_id = $reference_id;";
            _ = remove.Parameters.AddWithValue("$artifact_id", artifactId);
            _ = remove.Parameters.AddWithValue("$reference_id", referenceId);
            _ = remove.ExecuteNonQuery();
        }
    }

    public IReadOnlyList<ImageArtifactMetadata> RemoveStaleUnreferencedImageArtifacts(DateTimeOffset before)
    {
        var artifacts = new List<ImageArtifactMetadata>();
        lock (_database.Gate)
        {
            using var transaction = _database.Begin();
            using (var read = _database.Connection.CreateCommand())
            {
                read.Transaction = transaction;
                read.CommandText = ImageArtifactSelect
                    + " WHERE artifact.created_at < $before "
                    + "AND NOT EXISTS (SELECT 1 FROM image_artifact_reference AS reference WHERE reference.artifact_id = artifact.artifact_id) "
                    + "AND NOT EXISTS (SELECT 1 FROM input_part WHERE input_part.artifact_id = artifact.artifact_id) "
                    + "AND NOT EXISTS (SELECT 1 FROM conversation_part WHERE conversation_part.artifact_id = artifact.artifact_id) "
                    + "AND NOT EXISTS (SELECT 1 FROM tool_execution_result_part WHERE tool_execution_result_part.artifact_id = artifact.artifact_id);";
                _ = read.Parameters.AddWithValue("$before", before.UtcDateTime.ToString("O", System.Globalization.CultureInfo.InvariantCulture));
                using var reader = read.ExecuteReader();
                while (reader.Read())
                {
                    artifacts.Add(ReadImageArtifact(reader));
                }
            }

            foreach (var artifact in artifacts)
            {
                using (var uploads = _database.Connection.CreateCommand())
                {
                    uploads.Transaction = transaction;
                    uploads.CommandText = "DELETE FROM image_upload WHERE artifact_id = $artifact_id;";
                    _ = uploads.Parameters.AddWithValue("$artifact_id", artifact.ArtifactId);
                    _ = uploads.ExecuteNonQuery();
                }

                using (var remove = _database.Connection.CreateCommand())
                {
                    remove.Transaction = transaction;
                    remove.CommandText = "DELETE FROM image_artifact WHERE artifact_id = $artifact_id;";
                    _ = remove.Parameters.AddWithValue("$artifact_id", artifact.ArtifactId);
                    _ = remove.ExecuteNonQuery();
                }

                using var content = _database.Connection.CreateCommand();
                content.Transaction = transaction;
                content.CommandText =
                    "DELETE FROM image_content WHERE sha256 = $sha256 "
                    + "AND NOT EXISTS (SELECT 1 FROM image_artifact WHERE sha256 = $sha256);";
                _ = content.Parameters.AddWithValue("$sha256", artifact.Sha256);
                _ = content.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        return artifacts;
    }

    // Spelt out rather than derived from the enum name: the column outlives any
    // rename of the generated member.
    //
    // Neither of these is a caller's mistake -- the service refuses an
    // unspecified delivery before it reaches here, and the text read back is
    // text this wrote -- so neither is an InputConflictException, which the
    // service maps to "the sender contradicted itself".
    private static string Text(Delivery delivery) => delivery switch
    {
        Delivery.Steer => "steer",
        Delivery.Queue => "queue",
        _ => throw new ArgumentOutOfRangeException(nameof(delivery)),
    };

    private static Delivery Parse(string delivery) => delivery switch
    {
        "steer" => Delivery.Steer,
        "queue" => Delivery.Queue,
        _ => throw new InvalidOperationException($"the input table holds an unknown delivery {delivery}"),
    };

    private static void AddImageContent(SqliteCommand command, ImageArtifactMetadata artifact)
    {
        _ = command.Parameters.AddWithValue("$sha256", artifact.Sha256);
        _ = command.Parameters.AddWithValue("$media_type", artifact.MediaType);
        _ = command.Parameters.AddWithValue("$byte_length", artifact.ByteLength);
        _ = command.Parameters.AddWithValue("$width", artifact.Width);
        _ = command.Parameters.AddWithValue("$height", artifact.Height);
        _ = command.Parameters.AddWithValue("$frame_count", artifact.FrameCount);
        _ = command.Parameters.AddWithValue("$aggregate_pixels", artifact.AggregatePixels);
    }

    private static void AddPart(SqliteCommand command, object owner, int position, ConversationPart part)
    {
        _ = command.Parameters.AddWithValue("$owner", owner);
        _ = command.Parameters.AddWithValue("$position", position);
        _ = command.Parameters.AddWithValue("$kind", PartText(part.Kind));
        _ = command.Parameters.AddWithValue("$text", part.Text);
        _ = command.Parameters.AddWithValue("$artifact", part.ArtifactId);
        _ = command.Parameters.AddWithValue("$media", part.MediaType);
        _ = command.Parameters.AddWithValue("$display", part.DisplayName);
    }

    private static string PartText(ConversationPartKind kind) => kind switch
    {
        ConversationPartKind.Text => "text",
        ConversationPartKind.ImageArtifact => "image_artifact",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static ConversationPart ToPart(LLMContent content) => content.Kind switch
    {
        LLMContentKind.Text => ConversationPart.TextPart(content.Text),
        LLMContentKind.Image => throw new InvalidOperationException("image bytes must be persisted as an artifact reference"),
        _ => throw new ArgumentOutOfRangeException(nameof(content)),
    };

    private static ImageArtifactMetadata ReadImageArtifact(SqliteDataReader reader) => new(
        (string)reader["artifact_id"],
        (string)reader["sha256"],
        (string)reader["media_type"],
        Convert.ToInt64(reader["byte_length"], System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToInt32(reader["width"], System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToInt32(reader["height"], System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToInt32(reader["frame_count"], System.Globalization.CultureInfo.InvariantCulture),
        Convert.ToInt64(reader["aggregate_pixels"], System.Globalization.CultureInfo.InvariantCulture),
        (string)reader["display_name"],
        (string)reader["origin"]);

    private static string Timestamp() =>
        DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture);

    private static string TodoStatusText(TodoStatus status) => status switch
    {
        TodoStatus.Pending => "pending",
        TodoStatus.InProgress => "in_progress",
        TodoStatus.Completed => "completed",
        TodoStatus.Cancelled => "cancelled",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static TodoStatus ParseTodoStatus(string status) => status switch
    {
        "pending" => TodoStatus.Pending,
        "in_progress" => TodoStatus.InProgress,
        "completed" => TodoStatus.Completed,
        "cancelled" => TodoStatus.Cancelled,
        _ => throw new InvalidOperationException($"the todo table holds an unknown status {status}"),
    };

    private static string TodoPriorityText(TodoPriority priority) => priority switch
    {
        TodoPriority.High => "high",
        TodoPriority.Medium => "medium",
        TodoPriority.Low => "low",
        _ => throw new ArgumentOutOfRangeException(nameof(priority)),
    };

    private static TodoPriority ParseTodoPriority(string priority) => priority switch
    {
        "high" => TodoPriority.High,
        "medium" => TodoPriority.Medium,
        "low" => TodoPriority.Low,
        _ => throw new InvalidOperationException($"the todo table holds an unknown priority {priority}"),
    };

    private static ConversationOrigin Origin(string role) => role switch
    {
        "system" => ConversationOrigin.System,
        "user" => ConversationOrigin.UserInput,
        "assistant" => ConversationOrigin.Model,
        "tool" => ConversationOrigin.Tool,
        _ => throw new InvalidOperationException($"unknown conversation role {role}"),
    };

    private static string OriginText(ConversationOrigin origin) => origin switch
    {
        ConversationOrigin.System => "system",
        ConversationOrigin.UserInput => "user_input",
        ConversationOrigin.Model => "model",
        ConversationOrigin.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(origin)),
    };

    private static ConversationOrigin ParseOrigin(string origin) => origin switch
    {
        "system" => ConversationOrigin.System,
        "user_input" => ConversationOrigin.UserInput,
        "model" => ConversationOrigin.Model,
        "tool" => ConversationOrigin.Tool,
        _ => throw new InvalidOperationException($"unknown conversation origin {origin}"),
    };

    private static LLMRole ParseRole(string role) => role switch
    {
        "system" => LLMRole.System,
        "user" => LLMRole.User,
        "assistant" => LLMRole.Assistant,
        "tool" => LLMRole.Tool,
        _ => throw new InvalidOperationException($"unknown conversation role {role}"),
    };

    private static ConversationPart ReadPart(SqliteDataReader reader) => (string)reader["kind"] switch
    {
        "text" => ConversationPart.TextPart((string)reader["text"]),
        "image_artifact" => ConversationPart.ImageArtifact(
            (string)reader["artifact_id"], (string)reader["media_type"], (string)reader["display_name"]),
        var kind => throw new InvalidOperationException($"unknown conversation part kind {kind}"),
    };

    private static LLMMessage ToMessage(ConversationItem item)
    {
        if (item.Parts.Any(part => part.Kind == ConversationPartKind.ImageArtifact))
        {
            throw new InvalidOperationException("artifact references must be materialized before model history is read");
        }

        return new LLMMessage
        {
            Role = item.Role,
            Contents = [.. item.Parts.Select(part => LLMContent.TextPart(part.Text))],
            ToolCalls = item.ToolCalls,
            ToolCallId = item.ToolCallId,
        };
    }

    private static LLMMessage Message(string role, string content) => role switch
    {
        "system" => LLMMessage.System(content),
        "user" => LLMMessage.User(content),
        "assistant" => LLMMessage.Assistant(content, []),
        _ => throw new InvalidOperationException($"the message table holds an unknown role {role}"),
    };

    private static string Text(LLMRole role) => role switch
    {
        LLMRole.System => "system",
        LLMRole.User => "user",
        LLMRole.Assistant => "assistant",
        LLMRole.Tool => "tool",
        _ => throw new ArgumentOutOfRangeException(nameof(role)),
    };

    private static bool ToolTerminalsEqual(ToolExecutionTerminal left, ToolExecutionTerminal right) =>
        left.ToolCallId == right.ToolCallId
        && left.ToolName == right.ToolName
        && left.Status == right.Status
        && left.Message == right.Message
        && left.ResultParts.SequenceEqual(right.ResultParts);

    private static string ToolExecutionStatusText(ToolExecutionStatus status) => status switch
    {
        ToolExecutionStatus.Finished => "finished",
        ToolExecutionStatus.Cancelled => "cancelled",
        ToolExecutionStatus.Error => "error",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    private static ToolExecutionStatus ParseToolExecutionStatus(string status) => status switch
    {
        "finished" => ToolExecutionStatus.Finished,
        "cancelled" => ToolExecutionStatus.Cancelled,
        "error" => ToolExecutionStatus.Error,
        _ => throw new InvalidOperationException($"unknown tool execution status {status}"),
    };

    // The three writes a promotion is -- the input settles, the conversation
    // gains the message, the event becomes durable -- in one transaction, so no
    // reader can see a promoted input whose message is missing (principle 9).
    private ImageArtifactMetadata? ImageUpload(SqliteTransaction transaction, string uploadId)
    {
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = ImageArtifactSelect + " JOIN image_upload AS upload ON upload.artifact_id = artifact.artifact_id WHERE upload.upload_id = $upload_id;";
        _ = read.Parameters.AddWithValue("$upload_id", uploadId);
        using var reader = read.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ImageArtifactMetadata(
            (string)reader["artifact_id"],
            (string)reader["sha256"],
            (string)reader["media_type"],
            Convert.ToInt64(reader["byte_length"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["width"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["height"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["frame_count"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt64(reader["aggregate_pixels"], System.Globalization.CultureInfo.InvariantCulture),
            (string)reader["display_name"],
            (string)reader["origin"]);
    }

    private ImageArtifactMetadata? ResolveImageArtifact(SqliteTransaction transaction, string artifactId)
    {
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = ImageArtifactSelect + " WHERE artifact.artifact_id = $artifact_id;";
        _ = read.Parameters.AddWithValue("$artifact_id", artifactId);
        using var reader = read.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return new ImageArtifactMetadata(
            (string)reader["artifact_id"],
            (string)reader["sha256"],
            (string)reader["media_type"],
            Convert.ToInt64(reader["byte_length"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["width"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["height"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt32(reader["frame_count"], System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToInt64(reader["aggregate_pixels"], System.Globalization.CultureInfo.InvariantCulture),
            (string)reader["display_name"],
            (string)reader["origin"]);
    }

    private List<Promotion> Promote(
        string agentSessionId, Delivery delivery, int limit, Func<AdmittedInput, Event> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);

        lock (_database.Gate)
        {
            // Read before the transaction: the drain asks at every turn
            // boundary and almost always finds nothing, and BeginTransaction
            // takes the file's write lock before running a statement -- so
            // opening one first would make the empty answer the expensive one.
            var pending = Pending(agentSessionId, delivery, limit);

            if (pending.Count == 0)
            {
                return [];
            }

            using var transaction = _database.Begin();

            // One instant for the whole commit. Three statements landing
            // atomically should not record three different times.
            var at = Timestamp();
            var promoted = new List<Promotion>(pending.Count);

            foreach (var input in pending)
            {
                var published = compose(input);

                using (var settle = _database.Connection.CreateCommand())
                {
                    settle.Transaction = transaction;
                    settle.CommandText =
                        "UPDATE input SET status = 'promoted', promoted_at = $at WHERE id = $id AND status = 'pending';";
                    _ = settle.Parameters.AddWithValue("$at", at);
                    _ = settle.Parameters.AddWithValue("$id", input.Id);

                    // Nobody else may promote: every promotion runs behind
                    // the database gate, so a row that moved under one is the store
                    // disagreeing with itself, not a caller's mistake.
                    if (settle.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException($"input {input.Id} changed during promotion");
                    }
                }

                _ = Project(
                    transaction,
                    agentSessionId,
                    ConversationOrigin.UserInput,
                    LLMRole.User,
                    input.Parts,
                    [],
                    string.Empty);
                _ = Record(transaction, published);
                promoted.Add(new Promotion(input, published));
            }

            transaction.Commit();

            return promoted;
        }
    }

    // A negative limit is SQLite for "no limit", which is what lets one query
    // serve both promotions rather than two literals that can drift apart.
    private List<AdmittedInput> Pending(string agentSessionId, Delivery delivery, int limit)
    {
        using var read = _database.Connection.CreateCommand();
        read.CommandText =
            """
            SELECT id, message_id, content FROM input
            WHERE agent_session = $session AND delivery = $delivery AND status = 'pending'
            ORDER BY sequence LIMIT $limit;
            """;
        _ = read.Parameters.AddWithValue("$session", agentSessionId);
        _ = read.Parameters.AddWithValue("$delivery", Text(delivery));
        _ = read.Parameters.AddWithValue("$limit", limit);

        var rows = new List<(string Id, string MessageId, string Content)>();
        using (var reader = read.ExecuteReader())
        {
            while (reader.Read())
            {
                rows.Add(((string)reader["id"], (string)reader["message_id"], (string)reader["content"]));
            }
        }

        return [.. rows.Select(row => new AdmittedInput(
            row.Id, row.MessageId, ReadInputParts(null, row.Id, row.Content), delivery))];
    }

    private AdmittedInput? Existing(SqliteTransaction transaction, string agentSessionId, string messageId)
    {
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT id, content, delivery FROM input WHERE agent_session = $session AND message_id = $message;";
        _ = read.Parameters.AddWithValue("$session", agentSessionId);
        _ = read.Parameters.AddWithValue("$message", messageId);

        using var reader = read.ExecuteReader();

        if (!reader.Read())
        {
            return null;
        }

        var inputId = (string)reader["id"];
        var content = (string)reader["content"];
        var delivery = Parse((string)reader["delivery"]);
        reader.Close();
        return new AdmittedInput(inputId, messageId, ReadInputParts(transaction, inputId, content), delivery);
    }

    private List<ConversationPart> ReadInputParts(SqliteTransaction? transaction, string inputId, string legacyContent)
    {
        var parts = new List<ConversationPart>();
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT kind, text, artifact_id, media_type, display_name FROM input_part WHERE input_id = $id ORDER BY position;";
        _ = read.Parameters.AddWithValue("$id", inputId);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            parts.Add(ReadPart(reader));
        }

        return parts.Count == 0 ? [ConversationPart.TextPart(legacyContent)] : parts;
    }

    private void EnsureConversationProjection(SqliteTransaction transaction)
    {
        using var version = _database.Connection.CreateCommand();
        version.Transaction = transaction;
        version.CommandText = "SELECT version FROM projection_version WHERE name = $name;";
        _ = version.Parameters.AddWithValue("$name", ConversationProjection);
        var current = version.ExecuteScalar();
        if (current is not null
            && Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture)
                >= ConversationProjectionVersion)
        {
            return;
        }

        using (var count = _database.Connection.CreateCommand())
        {
            count.Transaction = transaction;
            count.CommandText = "SELECT COUNT(*) FROM conversation_item;";
            if (Convert.ToInt64(count.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) == 0)
            {
                using var backfill = _database.Connection.CreateCommand();
                backfill.Transaction = transaction;
                backfill.CommandText =
                    "INSERT INTO conversation_item (sequence, agent_session, origin, role, tool_call_id, created_at) "
                    + "SELECT sequence, agent_session, CASE role WHEN 'system' THEN 'system' "
                    + "WHEN 'user' THEN 'user_input' WHEN 'tool' THEN 'tool' ELSE 'model' END, "
                    + "role, '', created_at FROM message ORDER BY sequence;";
                _ = backfill.ExecuteNonQuery();

                using var parts = _database.Connection.CreateCommand();
                parts.Transaction = transaction;
                parts.CommandText =
                    "INSERT INTO conversation_part "
                    + "(item_sequence, position, kind, text, artifact_id, media_type, display_name) "
                    + "SELECT sequence, 0, 'text', content, '', '', '' FROM message ORDER BY sequence;";
                _ = parts.ExecuteNonQuery();
            }
        }

        using var mark = _database.Connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText = "INSERT OR REPLACE INTO projection_version (name, version) VALUES ($name, $version);";
        _ = mark.Parameters.AddWithValue("$name", ConversationProjection);
        _ = mark.Parameters.AddWithValue("$version", ConversationProjectionVersion);
        _ = mark.ExecuteNonQuery();
    }

    private bool HasToolResult(
        SqliteTransaction transaction,
        string agentSessionId,
        long assistantSequence,
        string toolCallId)
    {
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT EXISTS (SELECT 1 FROM tool_batch_result "
            + "WHERE agent_session = $session AND assistant_sequence = $assistant AND tool_call_id = $call);";
        _ = read.Parameters.AddWithValue("$session", agentSessionId);
        _ = read.Parameters.AddWithValue("$assistant", assistantSequence);
        _ = read.Parameters.AddWithValue("$call", toolCallId);
        return Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private bool HasToolSynthetic(
        SqliteTransaction transaction,
        string agentSessionId,
        long assistantSequence)
    {
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT EXISTS (SELECT 1 FROM tool_batch_synthetic "
            + "WHERE agent_session = $session AND assistant_sequence = $assistant);";
        _ = read.Parameters.AddWithValue("$session", agentSessionId);
        _ = read.Parameters.AddWithValue("$assistant", assistantSequence);
        return Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
    }

    private ToolExecutionTerminal? ReadToolTerminal(
        SqliteTransaction transaction,
        string agentSessionId,
        string toolCallId)
    {
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT sequence, tool_name, status, message FROM tool_execution_terminal "
            + "WHERE agent_session = $session AND tool_call_id = $call;";
        _ = read.Parameters.AddWithValue("$session", agentSessionId);
        _ = read.Parameters.AddWithValue("$call", toolCallId);
        using var reader = read.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        var sequence = Convert.ToInt64(reader["sequence"], System.Globalization.CultureInfo.InvariantCulture);
        return new ToolExecutionTerminal(
            toolCallId,
            (string)reader["tool_name"],
            ParseToolExecutionStatus((string)reader["status"]),
            ReadToolResultParts(transaction, sequence),
            (string)reader["message"]);
    }

    private List<ConversationPart> ReadToolResultParts(SqliteTransaction transaction, long terminalSequence)
    {
        var parts = new List<ConversationPart>();
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT kind, text, artifact_id, media_type, display_name FROM tool_execution_result_part "
            + "WHERE terminal_sequence = $sequence ORDER BY position;";
        _ = read.Parameters.AddWithValue("$sequence", terminalSequence);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            parts.Add(ReadPart(reader));
        }

        return parts;
    }

    private List<ConversationItem> ReadConversation(
        SqliteTransaction transaction,
        string agentSessionId,
        long watermark)
    {
        var items = new List<ConversationItem>();
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT sequence, origin, role, tool_call_id FROM conversation_item "
            + "WHERE agent_session = $session AND sequence > $watermark ORDER BY sequence;";
        _ = read.Parameters.AddWithValue("$session", agentSessionId);
        _ = read.Parameters.AddWithValue("$watermark", watermark);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            var sequence = Convert.ToInt64(reader["sequence"], System.Globalization.CultureInfo.InvariantCulture);
            items.Add(new ConversationItem(
                sequence,
                ParseOrigin((string)reader["origin"]),
                ParseRole((string)reader["role"]),
                ReadParts(transaction, sequence),
                ReadToolCalls(transaction, sequence),
                (string)reader["tool_call_id"]));
        }

        return items;
    }

    private List<ConversationPart> ReadParts(SqliteTransaction transaction, long sequence)
    {
        var parts = new List<ConversationPart>();
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT kind, text, artifact_id, media_type, display_name FROM conversation_part "
            + "WHERE item_sequence = $sequence ORDER BY position;";
        _ = read.Parameters.AddWithValue("$sequence", sequence);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            parts.Add(ReadPart(reader));
        }

        return parts;
    }

    private List<LLMToolCall> ReadToolCalls(SqliteTransaction transaction, long sequence)
    {
        var calls = new List<LLMToolCall>();
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT id, name, arguments FROM conversation_tool_call "
            + "WHERE item_sequence = $sequence ORDER BY position;";
        _ = read.Parameters.AddWithValue("$sequence", sequence);
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            calls.Add(new LLMToolCall((string)reader["id"], (string)reader["name"], (string)reader["arguments"]));
        }

        return calls;
    }

    private void EnsureUsageProjection(SqliteTransaction transaction)
    {
        using (var version = _database.Connection.CreateCommand())
        {
            version.Transaction = transaction;
            version.CommandText = "SELECT version FROM projection_version WHERE name = $name;";
            _ = version.Parameters.AddWithValue("$name", UsageProjection);
            var current = version.ExecuteScalar();
            if (current is not null
                && Convert.ToInt64(current, System.Globalization.CultureInfo.InvariantCulture) >= UsageProjectionVersion)
            {
                return;
            }
        }

        var latest = new Dictionary<string, (long Revision, AgentStatisticsUpdatedEvent Statistics)>(StringComparer.Ordinal);
        using (var read = _database.Connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT sequence, agent_session, payload FROM event ORDER BY sequence;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var published = Event.Parser.ParseFrom((byte[])reader["payload"]);
                if (published.PayloadCase != Event.PayloadOneofCase.AgentStatisticsUpdated)
                {
                    continue;
                }

                var revision = Convert.ToInt64(reader["sequence"], System.Globalization.CultureInfo.InvariantCulture);
                latest[(string)reader["agent_session"]] = (revision, published.AgentStatisticsUpdated);
            }
        }

        using (var clear = _database.Connection.CreateCommand())
        {
            clear.Transaction = transaction;
            clear.CommandText = "DELETE FROM agent_usage;";
            _ = clear.ExecuteNonQuery();
        }

        foreach (var (agentSessionId, entry) in latest)
        {
            ProjectUsage(transaction, agentSessionId, entry.Revision, entry.Statistics);
        }

        using var mark = _database.Connection.CreateCommand();
        mark.Transaction = transaction;
        mark.CommandText =
            "INSERT OR REPLACE INTO projection_version (name, version) VALUES ($name, $version);";
        _ = mark.Parameters.AddWithValue("$name", UsageProjection);
        _ = mark.Parameters.AddWithValue("$version", UsageProjectionVersion);
        _ = mark.ExecuteNonQuery();
    }

    private void ProjectUsage(
        SqliteTransaction transaction,
        string agentSessionId,
        long revision,
        AgentStatisticsUpdatedEvent statistics)
    {
        using var upsert = _database.Connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText =
            """
            INSERT INTO agent_usage (
                agent_session, revision, input_tokens, cached_input_tokens, output_tokens,
                context_size, context_limit, input_cost, output_cost)
            VALUES (
                $session, $revision, $input, $cached, $output,
                $context, $limit, $input_cost, $output_cost)
            ON CONFLICT(agent_session) DO UPDATE SET
                revision = excluded.revision,
                input_tokens = excluded.input_tokens,
                cached_input_tokens = excluded.cached_input_tokens,
                output_tokens = excluded.output_tokens,
                context_size = excluded.context_size,
                context_limit = excluded.context_limit,
                input_cost = excluded.input_cost,
                output_cost = excluded.output_cost;
            """;
        _ = upsert.Parameters.AddWithValue("$session", agentSessionId);
        _ = upsert.Parameters.AddWithValue("$revision", revision);
        _ = upsert.Parameters.AddWithValue("$input", statistics.InputTokens);
        _ = upsert.Parameters.AddWithValue("$cached", statistics.CachedInputTokens);
        _ = upsert.Parameters.AddWithValue("$output", statistics.OutputTokens);
        _ = upsert.Parameters.AddWithValue("$context", statistics.ContextSize);
        _ = upsert.Parameters.AddWithValue("$limit", statistics.ContextLimit);
        _ = upsert.Parameters.AddWithValue("$input_cost", statistics.InputCost);
        _ = upsert.Parameters.AddWithValue("$output_cost", statistics.OutputCost);
        _ = upsert.ExecuteNonQuery();
    }

    private SessionUsage CaptureUsage(SqliteTransaction transaction)
    {
        string? mainAgentSessionId;
        using (var main = _database.Connection.CreateCommand())
        {
            main.Transaction = transaction;
            main.CommandText = "SELECT agent_session FROM session_state LIMIT 1;";
            mainAgentSessionId = main.ExecuteScalar() as string;
        }

        long revision = 0;
        long inputTokens = 0;
        long cachedInputTokens = 0;
        long outputTokens = 0;
        long contextSize = 0;
        long contextLimit = 0;
        double inputCost = 0;
        double outputCost = 0;
        using var read = _database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            """
            SELECT agent_session, revision, input_tokens, cached_input_tokens, output_tokens,
                   context_size, context_limit, input_cost, output_cost
            FROM agent_usage;
            """;
        using var reader = read.ExecuteReader();
        while (reader.Read())
        {
            revision = Math.Max(
                revision,
                Convert.ToInt64(reader["revision"], System.Globalization.CultureInfo.InvariantCulture));
            inputTokens = checked(inputTokens
                + Convert.ToInt64(reader["input_tokens"], System.Globalization.CultureInfo.InvariantCulture));
            cachedInputTokens = checked(cachedInputTokens
                + Convert.ToInt64(reader["cached_input_tokens"], System.Globalization.CultureInfo.InvariantCulture));
            outputTokens = checked(outputTokens
                + Convert.ToInt64(reader["output_tokens"], System.Globalization.CultureInfo.InvariantCulture));
            inputCost += Convert.ToDouble(reader["input_cost"], System.Globalization.CultureInfo.InvariantCulture);
            outputCost += Convert.ToDouble(reader["output_cost"], System.Globalization.CultureInfo.InvariantCulture);
            if (mainAgentSessionId is not null
                && string.Equals((string)reader["agent_session"], mainAgentSessionId, StringComparison.Ordinal))
            {
                contextSize = Convert.ToInt64(reader["context_size"], System.Globalization.CultureInfo.InvariantCulture);
                contextLimit = Convert.ToInt64(reader["context_limit"], System.Globalization.CultureInfo.InvariantCulture);
            }
        }

        return revision == 0
            ? SessionUsage.Empty
            : new SessionUsage(
                checked((ulong)revision),
                inputTokens,
                cachedInputTokens,
                outputTokens,
                contextSize,
                contextLimit,
                inputCost,
                outputCost);
    }

    private long Record(SqliteTransaction transaction, Event published)
    {
        using var insert = _database.Connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO event (id, agent_session, payload, created_at) VALUES ($id, $session, $payload, $at);";
        _ = insert.Parameters.AddWithValue("$id", published.Id);
        _ = insert.Parameters.AddWithValue("$session", published.AgentSessionId);
        _ = insert.Parameters.AddWithValue("$payload", published.ToByteArray());
        _ = insert.Parameters.AddWithValue("$at", Timestamp());
        _ = insert.ExecuteNonQuery();

        using var sequence = _database.Connection.CreateCommand();
        sequence.Transaction = transaction;
        sequence.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(sequence.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void Project(SqliteTransaction transaction, string agentSessionId, string role, string content) =>
        Project(transaction, agentSessionId, Message(role, content), Origin(role));

    private long Project(
        SqliteTransaction transaction,
        string agentSessionId,
        ConversationOrigin origin,
        LLMRole role,
        IReadOnlyList<ConversationPart> parts,
        IReadOnlyList<LLMToolCall> toolCalls,
        string toolCallId)
    {
        EnsureConversationProjection(transaction);
        var createdAt = Timestamp();
        using (var legacy = _database.Connection.CreateCommand())
        {
            legacy.Transaction = transaction;
            legacy.CommandText =
                "INSERT INTO message (agent_session, role, content, created_at) VALUES ($session, $role, $content, $at);";
            _ = legacy.Parameters.AddWithValue("$session", agentSessionId);
            _ = legacy.Parameters.AddWithValue("$role", Text(role));
            _ = legacy.Parameters.AddWithValue("$content", string.Concat(
                parts.Where(part => part.Kind == ConversationPartKind.Text).Select(part => part.Text)));
            _ = legacy.Parameters.AddWithValue("$at", createdAt);
            _ = legacy.ExecuteNonQuery();
        }

        var sequence = InsertConversationItem(transaction, agentSessionId, origin, role, toolCallId, createdAt);
        InsertConversationParts(transaction, sequence, parts);
        InsertToolCalls(transaction, sequence, toolCalls);
        return sequence;
    }

    private void Project(
        SqliteTransaction transaction,
        string agentSessionId,
        LLMMessage message,
        ConversationOrigin origin)
    {
        EnsureConversationProjection(transaction);
        var createdAt = Timestamp();
        using (var legacy = _database.Connection.CreateCommand())
        {
            legacy.Transaction = transaction;
            legacy.CommandText =
                "INSERT INTO message (agent_session, role, content, created_at) VALUES ($session, $role, $content, $at);";
            _ = legacy.Parameters.AddWithValue("$session", agentSessionId);
            _ = legacy.Parameters.AddWithValue("$role", Text(message.Role));
            _ = legacy.Parameters.AddWithValue("$content", message.Content);
            _ = legacy.Parameters.AddWithValue("$at", createdAt);
            _ = legacy.ExecuteNonQuery();
        }

        var itemSequence = InsertConversationItem(
            transaction, agentSessionId, origin, message.Role, message.ToolCallId, createdAt);
        InsertConversationParts(transaction, itemSequence, [.. message.Contents.Select(ToPart)]);
        InsertToolCalls(transaction, itemSequence, message.ToolCalls);
    }

    private long InsertConversationItem(
        SqliteTransaction transaction,
        string agentSessionId,
        ConversationOrigin origin,
        LLMRole role,
        string toolCallId,
        string createdAt)
    {
        using var item = _database.Connection.CreateCommand();
        item.Transaction = transaction;
        item.CommandText =
            "INSERT INTO conversation_item (agent_session, origin, role, tool_call_id, created_at) "
            + "VALUES ($session, $origin, $role, $tool, $at);";
        _ = item.Parameters.AddWithValue("$session", agentSessionId);
        _ = item.Parameters.AddWithValue("$origin", OriginText(origin));
        _ = item.Parameters.AddWithValue("$role", Text(role));
        _ = item.Parameters.AddWithValue("$tool", toolCallId);
        _ = item.Parameters.AddWithValue("$at", createdAt);
        _ = item.ExecuteNonQuery();
        using var sequence = _database.Connection.CreateCommand();
        sequence.Transaction = transaction;
        sequence.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(sequence.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private void InsertInputParts(
        SqliteTransaction transaction,
        string inputId,
        IReadOnlyList<ConversationPart> parts)
    {
        for (var position = 0; position < parts.Count; position++)
        {
            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO input_part "
                + "(input_id, position, kind, text, artifact_id, media_type, display_name) "
                + "VALUES ($owner, $position, $kind, $text, $artifact, $media, $display);";
            AddPart(insert, inputId, position, parts[position]);
            _ = insert.ExecuteNonQuery();
        }
    }

    private void InsertToolResult(
        SqliteTransaction transaction,
        string agentSessionId,
        long assistantSequence,
        ToolExecutionTerminal terminal)
    {
        var resultParts = terminal.ResultParts
            .Where(part => part.Kind == ConversationPartKind.Text)
            .ToArray();
        if (resultParts.Length == 0)
        {
            resultParts = [ConversationPart.TextPart(terminal.Message)];
        }

        var sequence = Project(
            transaction,
            agentSessionId,
            ConversationOrigin.Tool,
            LLMRole.Tool,
            resultParts,
            [],
            terminal.ToolCallId);
        using var insert = _database.Connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO tool_batch_result (agent_session, assistant_sequence, tool_call_id, item_sequence) "
            + "VALUES ($session, $assistant, $call, $item);";
        _ = insert.Parameters.AddWithValue("$session", agentSessionId);
        _ = insert.Parameters.AddWithValue("$assistant", assistantSequence);
        _ = insert.Parameters.AddWithValue("$call", terminal.ToolCallId);
        _ = insert.Parameters.AddWithValue("$item", sequence);
        _ = insert.ExecuteNonQuery();
    }

    private void InsertToolTerminal(
        SqliteTransaction transaction,
        string agentSessionId,
        ToolExecutionTerminal terminal)
    {
        using var insert = _database.Connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO tool_execution_terminal (agent_session, tool_call_id, tool_name, status, message, created_at) "
            + "VALUES ($session, $call, $tool, $status, $message, $at);";
        _ = insert.Parameters.AddWithValue("$session", agentSessionId);
        _ = insert.Parameters.AddWithValue("$call", terminal.ToolCallId);
        _ = insert.Parameters.AddWithValue("$tool", terminal.ToolName);
        _ = insert.Parameters.AddWithValue("$status", ToolExecutionStatusText(terminal.Status));
        _ = insert.Parameters.AddWithValue("$message", terminal.Message);
        _ = insert.Parameters.AddWithValue("$at", Timestamp());
        _ = insert.ExecuteNonQuery();
        using var sequence = _database.Connection.CreateCommand();
        sequence.Transaction = transaction;
        sequence.CommandText = "SELECT last_insert_rowid();";
        var terminalSequence = Convert.ToInt64(
            sequence.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        InsertToolResultParts(transaction, terminalSequence, terminal.ResultParts);
    }

    private void InsertToolResultParts(
        SqliteTransaction transaction,
        long terminalSequence,
        IReadOnlyList<ConversationPart> parts)
    {
        for (var position = 0; position < parts.Count; position++)
        {
            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO tool_execution_result_part "
                + "(terminal_sequence, position, kind, text, artifact_id, media_type, display_name) "
                + "VALUES ($owner, $position, $kind, $text, $artifact, $media, $display);";
            AddPart(insert, terminalSequence, position, parts[position]);
            _ = insert.ExecuteNonQuery();
        }
    }

    private void InsertConversationParts(
        SqliteTransaction transaction,
        long sequence,
        IReadOnlyList<ConversationPart> parts)
    {
        for (var position = 0; position < parts.Count; position++)
        {
            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO conversation_part "
                + "(item_sequence, position, kind, text, artifact_id, media_type, display_name) "
                + "VALUES ($owner, $position, $kind, $text, $artifact, $media, $display);";
            AddPart(insert, sequence, position, parts[position]);
            _ = insert.ExecuteNonQuery();
        }
    }

    private void InsertToolCalls(
        SqliteTransaction transaction,
        long sequence,
        IReadOnlyList<LLMToolCall> calls)
    {
        for (var position = 0; position < calls.Count; position++)
        {
            using var insert = _database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO conversation_tool_call (item_sequence, position, id, name, arguments) "
                + "VALUES ($sequence, $position, $id, $name, $arguments);";
            _ = insert.Parameters.AddWithValue("$sequence", sequence);
            _ = insert.Parameters.AddWithValue("$position", position);
            _ = insert.Parameters.AddWithValue("$id", calls[position].Id);
            _ = insert.Parameters.AddWithValue("$name", calls[position].Name);
            _ = insert.Parameters.AddWithValue("$arguments", calls[position].ArgumentsJson);
            _ = insert.ExecuteNonQuery();
        }
    }
}

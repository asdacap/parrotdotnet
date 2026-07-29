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
// and the drain writes on its own, so every method here takes _gate. That gate
// is the whole of this component's synchronisation, and this component is the
// only thing that writes to the session database.
internal sealed class EventRepository(SessionDatabase database)
{
    private readonly Lock _gate = new();

    public void Append(Event published, string? messageRole, string? messageContent)
    {
        ArgumentNullException.ThrowIfNull(published);

        lock (_gate)
        {
            using var transaction = database.Begin();

            Record(transaction, published);

            // The projection, in the same transaction. Not a second write that
            // might not happen.
            if (messageRole is not null && messageContent is not null)
            {
                Project(transaction, published.AgentSessionId, messageRole, messageContent);
            }

            transaction.Commit();
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
        Func<AdmittedInput, Event> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);

        lock (_gate)
        {
            using var transaction = database.Begin();

            if (Existing(transaction, agentSessionId, messageId) is { } already)
            {
                return already.Content == content && already.Delivery == delivery
                    ? new Admission(already, null)
                    : throw new InputConflictException(
                        $"message {messageId} was already admitted with different content");
            }

            var admitted = new AdmittedInput(Identifier.InputId(), messageId, content, delivery);
            var published = compose(admitted);

            using (var insert = database.Connection.CreateCommand())
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
                _ = insert.Parameters.AddWithValue("$content", content);
                _ = insert.Parameters.AddWithValue("$delivery", Text(delivery));
                _ = insert.Parameters.AddWithValue("$at", Timestamp());
                _ = insert.ExecuteNonQuery();
            }

            Record(transaction, published);
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

        lock (_gate)
        {
            using var transaction = database.Begin();

            if (Existing(transaction, agentSessionId, messageId) is { } already)
            {
                return already.Content == content && already.Delivery == Delivery.Steer
                    ? new Admission(already, null)
                    : throw new InputConflictException(
                        $"message {messageId} was already admitted with different content");
            }

            using (var pending = database.Connection.CreateCommand())
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

            using (var insert = database.Connection.CreateCommand())
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

            Record(transaction, published);
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
        lock (_gate)
        {
            using var read = database.Connection.CreateCommand();
            read.CommandText =
                "SELECT EXISTS (SELECT 1 FROM input WHERE agent_session = $session AND status = 'pending');";
            _ = read.Parameters.AddWithValue("$session", agentSessionId);

            return Convert.ToInt64(read.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0;
        }
    }

    public IReadOnlyList<TodoItem> ReadTodos(string agentSessionId, CancellationToken cancellationToken)
    {
        var items = new List<TodoItem>();

        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var read = database.Connection.CreateCommand();
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
        lock (_gate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var transaction = database.Begin();
            using (var delete = database.Connection.CreateCommand())
            {
                delete.Transaction = transaction;
                delete.CommandText = "DELETE FROM todo WHERE agent_session = $session;";
                _ = delete.Parameters.AddWithValue("$session", agentSessionId);
                _ = delete.ExecuteNonQuery();
            }

            foreach (var item in items)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var insert = database.Connection.CreateCommand();
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

            Record(transaction, published);
            transaction.Commit();
        }
    }

    public IReadOnlyList<Event> Replay()
    {
        var events = new List<Event>();

        lock (_gate)
        {
            using var read = database.Connection.CreateCommand();
            read.CommandText = "SELECT payload FROM event ORDER BY sequence;";

            using var reader = read.ExecuteReader();

            while (reader.Read())
            {
                events.Add(Event.Parser.ParseFrom((byte[])reader["payload"]));
            }
        }

        return events;
    }

    public AgentStatistics? LatestStatistics(string agentSessionId)
    {
        lock (_gate)
        {
            using var read = database.Connection.CreateCommand();
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
        var messages = new List<LLMMessage>();

        lock (_gate)
        {
            using var read = database.Connection.CreateCommand();
            read.CommandText =
                "SELECT role, content FROM message WHERE agent_session = $session ORDER BY sequence;";
            _ = read.Parameters.AddWithValue("$session", agentSessionId);

            using var reader = read.ExecuteReader();

            while (reader.Read())
            {
                messages.Add(Message((string)reader["role"], (string)reader["content"]));
            }
        }

        return messages;
    }

    public (string AgentSessionId, string Mode) SessionState(string userSessionId, string requestedMode)
    {
        lock (_gate)
        {
            using var transaction = database.Begin();
            using var insert = database.Connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT OR IGNORE INTO session_state (user_session, agent_session, mode) VALUES ($user, $agent, $mode);";
            _ = insert.Parameters.AddWithValue("$user", userSessionId);
            _ = insert.Parameters.AddWithValue("$agent", Identifier.AgentSession());
            _ = insert.Parameters.AddWithValue("$mode", requestedMode);
            _ = insert.ExecuteNonQuery();

            using var read = database.Connection.CreateCommand();
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
        lock (_gate)
        {
            using var transaction = database.Begin();
            using var update = database.Connection.CreateCommand();
            update.Transaction = transaction;
            update.CommandText = "UPDATE session_state SET mode = $mode WHERE user_session = $user;";
            _ = update.Parameters.AddWithValue("$mode", mode);
            _ = update.Parameters.AddWithValue("$user", userSessionId);
            _ = update.ExecuteNonQuery();

            using var insert = database.Connection.CreateCommand();
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
        lock (_gate)
        {
            using var read = database.Connection.CreateCommand();
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

        lock (_gate)
        {
            using var transaction = database.Begin();
            using var pending = database.Connection.CreateCommand();
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

            using var insert = database.Connection.CreateCommand();
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

        lock (_gate)
        {
            using var transaction = database.Begin();
            Project(transaction, published.AgentSessionId, "system", content);
            transaction.Commit();
        }
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

    // The three writes a promotion is -- the input settles, the conversation
    // gains the message, the event becomes durable -- in one transaction, so no
    // reader can see a promoted input whose message is missing (principle 9).
    private List<Promotion> Promote(
        string agentSessionId, Delivery delivery, int limit, Func<AdmittedInput, Event> compose)
    {
        ArgumentNullException.ThrowIfNull(compose);

        lock (_gate)
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

            using var transaction = database.Begin();

            // One instant for the whole commit. Three statements landing
            // atomically should not record three different times.
            var at = Timestamp();
            var promoted = new List<Promotion>(pending.Count);

            foreach (var input in pending)
            {
                var published = compose(input);

                using (var settle = database.Connection.CreateCommand())
                {
                    settle.Transaction = transaction;
                    settle.CommandText =
                        "UPDATE input SET status = 'promoted', promoted_at = $at WHERE id = $id AND status = 'pending';";
                    _ = settle.Parameters.AddWithValue("$at", at);
                    _ = settle.Parameters.AddWithValue("$id", input.Id);

                    // Nobody else may promote: every promotion runs behind
                    // _gate, so a row that moved under one is the store
                    // disagreeing with itself, not a caller's mistake.
                    if (settle.ExecuteNonQuery() != 1)
                    {
                        throw new InvalidOperationException($"input {input.Id} changed during promotion");
                    }
                }

                Project(transaction, agentSessionId, "user", input.Content);
                Record(transaction, published);
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
        using var read = database.Connection.CreateCommand();
        read.CommandText =
            """
            SELECT id, message_id, content FROM input
            WHERE agent_session = $session AND delivery = $delivery AND status = 'pending'
            ORDER BY sequence LIMIT $limit;
            """;
        _ = read.Parameters.AddWithValue("$session", agentSessionId);
        _ = read.Parameters.AddWithValue("$delivery", Text(delivery));
        _ = read.Parameters.AddWithValue("$limit", limit);

        var pending = new List<AdmittedInput>();

        using var reader = read.ExecuteReader();

        while (reader.Read())
        {
            pending.Add(new AdmittedInput(
                (string)reader["id"], (string)reader["message_id"], (string)reader["content"], delivery));
        }

        return pending;
    }

    private AdmittedInput? Existing(SqliteTransaction transaction, string agentSessionId, string messageId)
    {
        using var read = database.Connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText =
            "SELECT id, content, delivery FROM input WHERE agent_session = $session AND message_id = $message;";
        _ = read.Parameters.AddWithValue("$session", agentSessionId);
        _ = read.Parameters.AddWithValue("$message", messageId);

        using var reader = read.ExecuteReader();

        return reader.Read()
            ? new AdmittedInput(
                (string)reader["id"], messageId, (string)reader["content"], Parse((string)reader["delivery"]))
            : null;
    }

    private void Record(SqliteTransaction transaction, Event published)
    {
        using var insert = database.Connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT INTO event (id, agent_session, payload, created_at) VALUES ($id, $session, $payload, $at);";
        _ = insert.Parameters.AddWithValue("$id", published.Id);
        _ = insert.Parameters.AddWithValue("$session", published.AgentSessionId);
        _ = insert.Parameters.AddWithValue("$payload", published.ToByteArray());
        _ = insert.Parameters.AddWithValue("$at", Timestamp());
        _ = insert.ExecuteNonQuery();
    }

    private void Project(SqliteTransaction transaction, string agentSessionId, string role, string content)
    {
        using var project = database.Connection.CreateCommand();
        project.Transaction = transaction;
        project.CommandText =
            "INSERT INTO message (agent_session, role, content, created_at) VALUES ($session, $role, $content, $at);";
        _ = project.Parameters.AddWithValue("$session", agentSessionId);
        _ = project.Parameters.AddWithValue("$role", role);
        _ = project.Parameters.AddWithValue("$content", content);
        _ = project.Parameters.AddWithValue("$at", Timestamp());
        _ = project.ExecuteNonQuery();
    }
}

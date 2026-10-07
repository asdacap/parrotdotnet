using Microsoft.Data.Sqlite;
using Parrot.Protocol;

namespace Parrot.Store;

// One SQLite file per user session, written by exactly one machine.
//
// journal_mode=TRUNCATE is a correctness requirement, not a preference. WAL
// coordinates through a memory-mapped -shm file, and two hosts mapping one file
// over a network filesystem get incoherent private views rather than shared
// state. No -shm or -wal may ever appear under the state directory.
internal sealed class SessionDatabase : IDisposable
{
    private SessionDatabase(SqliteConnection connection) => Connection = connection;

    // Callers assign CommandText from a literal at their own call site, which
    // is what lets CA2100 see that no query is built from input.
    public SqliteConnection Connection { get; }

    public Lock Gate { get; } = new();

    public static SessionDatabase Open(string path)
    {
        // An in-memory database has no directory, and neither does a bare
        // filename.
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            _ = Directory.CreateDirectory(directory);
        }

        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());

        connection.Open();

        using (var pragma = connection.CreateCommand())
        {
            pragma.CommandText = "PRAGMA journal_mode=TRUNCATE; PRAGMA foreign_keys=ON;";
            _ = pragma.ExecuteNonQuery();
        }

        using (var schema = connection.CreateCommand())
        {
            schema.CommandText =
                """
                CREATE TABLE IF NOT EXISTS event (
                    sequence      INTEGER PRIMARY KEY AUTOINCREMENT,
                    id            TEXT NOT NULL UNIQUE,
                    agent_session TEXT NOT NULL,
                    payload       BLOB NOT NULL,
                    created_at    TEXT NOT NULL,
                    request_id    TEXT,
                    tool_call_id  TEXT
                );

                CREATE TABLE IF NOT EXISTS message (
                    sequence      INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session TEXT NOT NULL,
                    role          TEXT NOT NULL,
                    content       TEXT NOT NULL,
                    created_at    TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS message_by_session ON message (agent_session, sequence);

                CREATE TABLE IF NOT EXISTS conversation_item (
                    sequence      INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session TEXT NOT NULL,
                    origin        TEXT NOT NULL,
                    role          TEXT NOT NULL,
                    tool_call_id  TEXT NOT NULL,
                    created_at    TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS conversation_by_session
                    ON conversation_item (agent_session, sequence);

                CREATE TABLE IF NOT EXISTS conversation_part (
                    item_sequence INTEGER NOT NULL REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    position      INTEGER NOT NULL CHECK (position >= 0),
                    kind          TEXT NOT NULL,
                    text          TEXT NOT NULL,
                    artifact_id   TEXT NOT NULL,
                    media_type    TEXT NOT NULL,
                    display_name  TEXT NOT NULL,
                    PRIMARY KEY (item_sequence, position)
                );

                CREATE TABLE IF NOT EXISTS conversation_tool_call (
                    item_sequence INTEGER NOT NULL REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    position      INTEGER NOT NULL CHECK (position >= 0),
                    id            TEXT NOT NULL,
                    name          TEXT NOT NULL,
                    arguments     TEXT NOT NULL,
                    PRIMARY KEY (item_sequence, position)
                );

                CREATE TABLE IF NOT EXISTS input (
                    sequence      INTEGER PRIMARY KEY AUTOINCREMENT,
                    id            TEXT NOT NULL UNIQUE,
                    agent_session TEXT NOT NULL,
                    message_id    TEXT NOT NULL,
                    content       TEXT NOT NULL,
                    delivery      TEXT NOT NULL,
                    status        TEXT NOT NULL,
                    created_at    TEXT NOT NULL,
                    promoted_at   TEXT
                );

                -- The sender's message id is what makes admission idempotent, so
                -- a re-send after a dropped connection cannot admit twice.
                CREATE UNIQUE INDEX IF NOT EXISTS input_by_message ON input (agent_session, message_id);

                CREATE INDEX IF NOT EXISTS input_pending ON input (agent_session, status, sequence);

                CREATE TABLE IF NOT EXISTS input_part (
                    input_id      TEXT NOT NULL REFERENCES input(id) ON DELETE CASCADE,
                    position      INTEGER NOT NULL CHECK (position >= 0),
                    kind          TEXT NOT NULL,
                    text          TEXT NOT NULL,
                    artifact_id   TEXT NOT NULL,
                    media_type    TEXT NOT NULL,
                    display_name  TEXT NOT NULL,
                    PRIMARY KEY (input_id, position)
                );

                CREATE TABLE IF NOT EXISTS tool_execution_terminal (
                    sequence      INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session TEXT NOT NULL,
                    tool_call_id  TEXT NOT NULL,
                    tool_name     TEXT NOT NULL,
                    status        TEXT NOT NULL,
                    message       TEXT NOT NULL,
                    created_at    TEXT NOT NULL,
                    UNIQUE (agent_session, tool_call_id)
                );

                CREATE TABLE IF NOT EXISTS tool_execution_result_part (
                    terminal_sequence INTEGER NOT NULL REFERENCES tool_execution_terminal(sequence) ON DELETE CASCADE,
                    position          INTEGER NOT NULL CHECK (position >= 0),
                    kind              TEXT NOT NULL,
                    text              TEXT NOT NULL,
                    artifact_id       TEXT NOT NULL,
                    media_type        TEXT NOT NULL,
                    display_name      TEXT NOT NULL,
                    PRIMARY KEY (terminal_sequence, position)
                );

                CREATE TABLE IF NOT EXISTS tool_batch_result (
                    agent_session     TEXT NOT NULL,
                    assistant_sequence INTEGER NOT NULL REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    tool_call_id      TEXT NOT NULL,
                    item_sequence     INTEGER NOT NULL REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    PRIMARY KEY (agent_session, assistant_sequence, tool_call_id),
                    UNIQUE (agent_session, tool_call_id)
                );

                CREATE TABLE IF NOT EXISTS tool_batch_synthetic (
                    agent_session      TEXT NOT NULL,
                    assistant_sequence INTEGER NOT NULL REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    item_sequence      INTEGER NOT NULL REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    PRIMARY KEY (agent_session, assistant_sequence),
                    UNIQUE (item_sequence)
                );

                CREATE TABLE IF NOT EXISTS compaction_snapshot (
                    agent_session TEXT PRIMARY KEY,
                    summary       TEXT NOT NULL,
                    watermark     INTEGER NOT NULL CHECK (watermark >= 0),
                    created_at    TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS compaction_status (
                    agent_session               TEXT NOT NULL,
                    watermark                   INTEGER NOT NULL CHECK (watermark >= 0),
                    status_conversation_sequence INTEGER NOT NULL REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    PRIMARY KEY (agent_session, watermark),
                    UNIQUE (status_conversation_sequence)
                );

                CREATE TABLE IF NOT EXISTS agent_history (
                    sequence              INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session         TEXT NOT NULL,
                    kind                  TEXT NOT NULL CHECK (kind IN ('message', 'compaction')),
                    conversation_sequence INTEGER REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    summary               TEXT NOT NULL,
                    watermark             INTEGER NOT NULL CHECK (watermark >= 0),
                    created_at            TEXT NOT NULL,
                    CHECK (
                        (kind = 'message' AND conversation_sequence IS NOT NULL AND summary = '' AND watermark = 0)
                        OR
                        (kind = 'compaction' AND conversation_sequence IS NULL)
                    ),
                    UNIQUE (conversation_sequence)
                );

                CREATE TABLE IF NOT EXISTS request_history_anchor (
                    event_sequence INTEGER PRIMARY KEY REFERENCES event(sequence),
                    agent_session TEXT NOT NULL,
                    history_sequence INTEGER NOT NULL
                );

                CREATE INDEX IF NOT EXISTS agent_history_by_session
                    ON agent_history (agent_session, sequence);

                CREATE UNIQUE INDEX IF NOT EXISTS agent_history_compaction
                    ON agent_history (agent_session, watermark) WHERE kind = 'compaction';

                CREATE TABLE IF NOT EXISTS history_checkpoint (
                    sequence           INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session      TEXT NOT NULL,
                    title              TEXT NOT NULL,
                    assistant_sequence INTEGER NOT NULL REFERENCES conversation_item(sequence) ON DELETE CASCADE,
                    tool_call_id        TEXT NOT NULL,
                    created_at          TEXT NOT NULL,
                    UNIQUE (agent_session, tool_call_id)
                );

                CREATE INDEX IF NOT EXISTS history_checkpoint_by_title
                    ON history_checkpoint (agent_session, title, sequence DESC);

                CREATE TABLE IF NOT EXISTS mode_change (
                    sequence      INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session TEXT NOT NULL,
                    mode          TEXT NOT NULL,
                    created_at    TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS mode_change_by_session
                    ON mode_change (agent_session, sequence);

                CREATE TABLE IF NOT EXISTS status_prompt (
                    sequence             INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session        TEXT NOT NULL,
                    mode_change_sequence INTEGER NOT NULL,
                    created_at           TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS status_prompt_by_session
                    ON status_prompt (agent_session, sequence);

                CREATE TABLE IF NOT EXISTS final_provider_request_prompt (
                    agent_session TEXT PRIMARY KEY,
                    created_at    TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS context_reminder (
                    sequence        INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session   TEXT NOT NULL,
                    canonical_model TEXT NOT NULL,
                    context_limit   INTEGER NOT NULL CHECK (context_limit > 0),
                    percentage      INTEGER NOT NULL CHECK (percentage > 0),
                    created_at      TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS context_reminder_by_session
                    ON context_reminder (agent_session, sequence DESC);

                CREATE TABLE IF NOT EXISTS session_state (
                    user_session  TEXT PRIMARY KEY,
                    agent_session TEXT NOT NULL,
                    mode          TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS agent_usage (
                    agent_session      TEXT PRIMARY KEY,
                    revision           INTEGER NOT NULL CHECK (revision > 0),
                    input_tokens       INTEGER NOT NULL,
                    cached_input_tokens INTEGER NOT NULL,
                    output_tokens      INTEGER NOT NULL,
                    context_size       INTEGER NOT NULL,
                    context_limit      INTEGER NOT NULL,
                    input_cost         REAL NOT NULL,
                    output_cost        REAL NOT NULL
                );

                CREATE TABLE IF NOT EXISTS image_content (
                    sha256           TEXT PRIMARY KEY,
                    media_type       TEXT NOT NULL,
                    byte_length      INTEGER NOT NULL CHECK (byte_length >= 0),
                    width            INTEGER NOT NULL CHECK (width > 0),
                    height           INTEGER NOT NULL CHECK (height > 0),
                    frame_count      INTEGER NOT NULL CHECK (frame_count > 0),
                    aggregate_pixels INTEGER NOT NULL CHECK (aggregate_pixels > 0)
                );

                CREATE TABLE IF NOT EXISTS image_artifact (
                    artifact_id  TEXT PRIMARY KEY,
                    sha256       TEXT NOT NULL REFERENCES image_content (sha256),
                    display_name TEXT NOT NULL,
                    origin       TEXT NOT NULL,
                    created_at   TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS image_upload (
                    upload_id   TEXT PRIMARY KEY,
                    artifact_id TEXT NOT NULL REFERENCES image_artifact (artifact_id),
                    created_at  TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS image_artifact_reference (
                    artifact_id  TEXT NOT NULL REFERENCES image_artifact (artifact_id),
                    reference_id TEXT NOT NULL,
                    PRIMARY KEY (artifact_id, reference_id)
                );

                CREATE INDEX IF NOT EXISTS image_artifact_unreferenced
                    ON image_artifact (created_at);

                CREATE TABLE IF NOT EXISTS projection_version (
                    name    TEXT PRIMARY KEY,
                    version INTEGER NOT NULL
                );
                """;
            _ = schema.ExecuteNonQuery();
        }

        MigrateEventUsageKeys(connection);
        MigrateAgentLineage(connection);
        using (var indexes = connection.CreateCommand())
        {
            indexes.CommandText =
                """
                CREATE INDEX IF NOT EXISTS event_by_session
                    ON event (agent_session, sequence);

                CREATE INDEX IF NOT EXISTS event_request
                    ON event (agent_session, request_id) WHERE request_id IS NOT NULL;

                CREATE INDEX IF NOT EXISTS event_request_by_sequence
                    ON event (agent_session, sequence) WHERE request_id IS NOT NULL;

                CREATE INDEX IF NOT EXISTS event_tool_call
                    ON event (agent_session, tool_call_id) WHERE tool_call_id IS NOT NULL;
                """;
            _ = indexes.ExecuteNonQuery();
        }

        return new SessionDatabase(connection);
    }

    // A usage fact's identity, lifted out of the payload so it can be indexed:
    // a request carries only its request id, a tool start only its call id,
    // and every other event neither.
    public static (string? RequestId, string? ToolCallId) ExtractUsageKeys(Event published)
    {
        ArgumentNullException.ThrowIfNull(published);
        return published.PayloadCase switch
        {
            Event.PayloadOneofCase.RequestUsageRecorded => (published.RequestUsageRecorded.RequestId, null),
            Event.PayloadOneofCase.ToolExecutionStarted => (null, published.ToolExecutionStarted.ToolCallId),
            _ => (null, null),
        };
    }

    // An agent's first start fixes its lineage; a later start of the same
    // agent changes nothing.
    public static void RecordAgentStart(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long eventSequence,
        Event published)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(published);
        if (published.PayloadCase != Event.PayloadOneofCase.AgentStarted)
        {
            return;
        }

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText =
            "INSERT OR IGNORE INTO agent_lineage (agent_session, parent_agent_session, name, event_sequence) "
            + "VALUES ($session, $parent, $name, $sequence);";
        _ = insert.Parameters.AddWithValue("$session", published.AgentSessionId);
        _ = insert.Parameters.AddWithValue("$parent", published.AgentStarted.ParentAgentSessionId);
        _ = insert.Parameters.AddWithValue("$name", published.AgentStarted.Name);
        _ = insert.Parameters.AddWithValue("$sequence", eventSequence);
        _ = insert.ExecuteNonQuery();
    }

    public string JournalMode()
    {
        using var command = Connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode;";
        return command.ExecuteScalar() as string ?? string.Empty;
    }

    public SqliteTransaction Begin() => Connection.BeginTransaction();

    public void Dispose()
    {
        Connection.Close();
        Connection.Dispose();
    }

    // A database created before the usage key columns gains them, filled from
    // every existing payload. One transaction, so a column that exists has
    // always been filled.
    private static void MigrateEventUsageKeys(SqliteConnection connection)
    {
        using (var columns = connection.CreateCommand())
        {
            columns.CommandText = "SELECT EXISTS (SELECT 1 FROM pragma_table_info('event') WHERE name = 'request_id');";
            if (Convert.ToInt64(columns.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        using (var alter = connection.CreateCommand())
        {
            alter.Transaction = transaction;
            alter.CommandText = "ALTER TABLE event ADD COLUMN request_id TEXT; ALTER TABLE event ADD COLUMN tool_call_id TEXT;";
            _ = alter.ExecuteNonQuery();
        }

        var keys = new List<(long Sequence, string? RequestId, string? ToolCallId)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT sequence, payload FROM event;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var (requestId, toolCallId) = ExtractUsageKeys(Event.Parser.ParseFrom((byte[])reader["payload"]));
                if (requestId is not null || toolCallId is not null)
                {
                    keys.Add((Convert.ToInt64(reader["sequence"], System.Globalization.CultureInfo.InvariantCulture), requestId, toolCallId));
                }
            }
        }

        using (var update = connection.CreateCommand())
        {
            update.Transaction = transaction;
            update.CommandText = "UPDATE event SET request_id = $request, tool_call_id = $call WHERE sequence = $sequence;";
            var request = update.Parameters.Add("$request", SqliteType.Text);
            var call = update.Parameters.Add("$call", SqliteType.Text);
            var sequence = update.Parameters.Add("$sequence", SqliteType.Integer);
            foreach (var (eventSequence, requestId, toolCallId) in keys)
            {
                request.Value = (object?)requestId ?? DBNull.Value;
                call.Value = (object?)toolCallId ?? DBNull.Value;
                sequence.Value = eventSequence;
                _ = update.ExecuteNonQuery();
            }
        }

        transaction.Commit();
    }

    // The lineage table is created here rather than in the schema, so that a
    // table that exists has always been filled from every earlier start.
    private static void MigrateAgentLineage(SqliteConnection connection)
    {
        using (var exists = connection.CreateCommand())
        {
            exists.CommandText = "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = 'agent_lineage');";
            if (Convert.ToInt64(exists.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) != 0)
            {
                return;
            }
        }

        using var transaction = connection.BeginTransaction();
        using (var create = connection.CreateCommand())
        {
            create.Transaction = transaction;
            create.CommandText =
                """
                CREATE TABLE agent_lineage (
                    agent_session        TEXT PRIMARY KEY,
                    parent_agent_session TEXT NOT NULL,
                    name                 TEXT NOT NULL,
                    event_sequence       INTEGER NOT NULL REFERENCES event(sequence)
                );
                """;
            _ = create.ExecuteNonQuery();
        }

        var starts = new List<(long Sequence, Event Published)>();
        using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = "SELECT sequence, payload FROM event ORDER BY sequence;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                var published = Event.Parser.ParseFrom((byte[])reader["payload"]);
                if (published.PayloadCase == Event.PayloadOneofCase.AgentStarted)
                {
                    starts.Add((Convert.ToInt64(reader["sequence"], System.Globalization.CultureInfo.InvariantCulture), published));
                }
            }
        }

        foreach (var (eventSequence, published) in starts)
        {
            RecordAgentStart(connection, transaction, eventSequence, published);
        }

        transaction.Commit();
    }
}

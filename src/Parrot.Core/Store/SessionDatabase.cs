using Microsoft.Data.Sqlite;

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
                    created_at    TEXT NOT NULL
                );

                CREATE TABLE IF NOT EXISTS message (
                    sequence      INTEGER PRIMARY KEY AUTOINCREMENT,
                    agent_session TEXT NOT NULL,
                    role          TEXT NOT NULL,
                    content       TEXT NOT NULL,
                    created_at    TEXT NOT NULL
                );

                CREATE INDEX IF NOT EXISTS message_by_session ON message (agent_session, sequence);

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

                CREATE TABLE IF NOT EXISTS todo (
                    agent_session TEXT NOT NULL,
                    id            TEXT NOT NULL,
                    position      INTEGER NOT NULL CHECK (position >= 0),
                    content       TEXT NOT NULL,
                    status        TEXT NOT NULL,
                    priority      TEXT NOT NULL,
                    PRIMARY KEY (agent_session, id),
                    UNIQUE (agent_session, position)
                );

                CREATE INDEX IF NOT EXISTS todo_by_session ON todo (agent_session, position);
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

                CREATE TABLE IF NOT EXISTS projection_version (
                    name    TEXT PRIMARY KEY,
                    version INTEGER NOT NULL
                );
                """;
            _ = schema.ExecuteNonQuery();
        }

        return new SessionDatabase(connection);
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
}

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

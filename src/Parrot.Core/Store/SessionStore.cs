using Parrot.Agent;

namespace Parrot.Store;

// Ties the pieces of the storage layout together: claim the working directory,
// open that session's database, publish its meta.json.
//
// A session is started automatically when the working directory differs,
// because the claim is keyed by directory. Starting parrot where a live session
// already exists yields a second one rather than joining it.
internal sealed class SessionStore(
    string stateDirectory,
    string workingDirectory,
    string hostKey,
    Func<string, string, EventRepository, UserSession> newUserSession) : IDisposable
{
    private readonly List<SessionDatabase> _open = [];

    public SessionIndex Index { get; } = new(stateDirectory);

    public UserSession Open(string model)
    {
        var claim = new WorkingDirectoryClaim(stateDirectory, hostKey);
        var claimed = claim.Claim(workingDirectory, Identifier.UserSession(), ProcessIsAlive);

        // A live binding means somebody else is already working here, so this
        // process takes its own session rather than joining or stealing.
        var id = claimed.Disposition == ClaimDisposition.Live ? Identifier.UserSession() : claimed.SessionId;

        var database = SessionDatabase.Open(Index.DatabaseFor(id));
        _open.Add(database);

        Index.Publish(new SessionMeta
        {
            Id = id,
            WorkingDirectory = workingDirectory,
            HostKey = hostKey,
            Model = model,
            ProcessId = Environment.ProcessId,
            CreatedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
        });

        return newUserSession(id, model, new EventRepository(database));
    }

    public void Dispose()
    {
        foreach (var database in _open)
        {
            database.Dispose();
        }

        _open.Clear();
    }

    // A record left by a dead process is abandoned and may be reclaimed. Repair
    // never ranges across sessions: this only ever asks about a pid on this
    // host, because a pid from another host names a process we cannot see.
    private static bool ProcessIsAlive(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}

using Parrot.Agent;
using Parrot.Llm;

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
    IUserSessionFactory userSessions,
    ModelRouter router,
    ModeRegistry modes) : IDisposable
{
    private readonly List<SessionDatabase> _open = [];

    public SessionIndex Index { get; } = new(stateDirectory);

    public UserSession Open(ResolvedModelSelection model) => Open(model, modes.Default);

    public UserSession Open(ResolvedModelSelection model, string mode)
    {
        var claim = new WorkingDirectoryClaim(stateDirectory, hostKey);
        var claimed = claim.Claim(workingDirectory, Identifier.UserSession(), ProcessIsAlive);

        // A live binding means somebody else is already working here, so this
        // process takes its own session rather than joining or stealing.
        var id = claimed.Disposition == ClaimDisposition.Live ? Identifier.UserSession() : claimed.SessionId;

        var existing = Index.Find(id);
        var rootAgentName = existing?.RootAgentName;

        if (string.IsNullOrEmpty(rootAgentName))
        {
            var names = Index.List()
                .Select(meta => meta.RootAgentName)
                .ToHashSet(StringComparer.Ordinal);
            rootAgentName = "main";

            for (var suffix = 2; !names.Add(rootAgentName); suffix++)
            {
                rootAgentName = $"main-{suffix}";
            }
        }

        SessionDatabase? database = null;

        try
        {
            database = SessionDatabase.Open(Index.DatabaseFor(id));
            var selected = existing is null ? model : router.Resolve(StoredSelector(existing));
            var session = userSessions.Create(id, rootAgentName, selected, mode, new EventRepository(database));
            Index.Publish(new SessionMeta
            {
                Id = id,
                WorkingDirectory = workingDirectory,
                HostKey = hostKey,
                RootAgentName = rootAgentName,
                ProviderId = session.ProviderId,
                Model = session.CanonicalModel,
                Selector = session.Model,
                Mode = session.Mode.Id,
                ProcessId = Environment.ProcessId,
                CreatedAt = existing?.CreatedAt
                    ?? DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });
            _open.Add(database);
            return session;
        }
        catch
        {
            database?.Dispose();
            throw;
        }
    }

    public void Publish(UserSession session)
    {
        var current = Index.List().Single(meta => string.Equals(meta.Id, session.Id, StringComparison.Ordinal));
        Index.Publish(current with
        {
            ProviderId = session.ProviderId,
            Model = session.CanonicalModel,
            Selector = session.Model,
            Mode = session.Mode.Id,
        });
    }

    public void Dispose()
    {
        foreach (var database in _open)
        {
            database.Dispose();
        }

        _open.Clear();
    }

    private static string StoredSelector(SessionMeta meta)
    {
        if (!string.IsNullOrEmpty(meta.Selector))
        {
            return meta.Selector;
        }

        return meta.Model.StartsWith($"{meta.ProviderId}/", StringComparison.Ordinal)
            ? meta.Model
            : $"{meta.ProviderId}/{meta.Model}";
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

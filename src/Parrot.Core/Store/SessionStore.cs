using Parrot.Agent;
using Parrot.Llm;
using Parrot.State;

namespace Parrot.Store;

internal sealed class SessionStore(
    StatePaths paths,
    string workingDirectory,
    string hostKey,
    IUserSessionFactory userSessions,
    ModelRouter router,
    ModeRegistry modes)
{
    public static void Publish(UserSession session)
    {
        lock (session.Resources.MetadataGate)
        {
            var index = new SessionIndex(session.Resources);
            var current = index.Find()
                ?? throw new InvalidOperationException(
                    $"Session metadata for '{session.Id}' does not belong to this session.");
            index.Publish(current with
            {
                ProviderId = session.ProviderId,
                Model = session.CanonicalModel,
                Selector = session.Model,
                Mode = session.Mode.Id,
            });
        }
    }

    public static void RecordOpened(UserSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        lock (session.Resources.MetadataGate)
        {
            var index = new SessionIndex(session.Resources);
            var current = index.Find()
                ?? throw new InvalidOperationException($"Session metadata for '{session.Id}' is unavailable.");
            index.Publish(current with
            {
                LastOpenedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });
        }
    }

    public AdmissionResult DiscoverLatest() =>
        new WorkingDirectoryClaim(paths.State, hostKey).DiscoverLatest(workingDirectory);

    public OpenedSession Resume(UserSessionId id, bool interactivePermissions)
    {
        var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
        var admission = new WorkingDirectoryClaim(paths.State, hostKey).Resume(workingDirectory, id);
        if (admission.ActivationLease is null)
        {
            throw new SessionAdmissionException(admission);
        }

        try
        {
            var existing = new SessionIndex(new UserSessionResources(paths, id, workspace)).Find()
                ?? throw new InvalidOperationException($"Session metadata for '{id}' is unavailable.");
            return Open(router.Resolve(StoredSelector(existing)), modes.Default, interactivePermissions, workspace, admission);
        }
        catch
        {
            admission.ActivationLease.Dispose();
            throw;
        }
    }

    public UserSession Open(ResolvedModelSelection model) => Open(model, modes.Default, false).Session;

    public OpenedSession Open(ResolvedModelSelection model, string mode, bool interactivePermissions)
    {
        var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
        var claim = new WorkingDirectoryClaim(paths.State, hostKey);
        var admission = claim.OpenDefault(workspace.LaunchDirectory);
        return Open(model, mode, interactivePermissions, workspace, admission);
    }

    public UserSession CreateFresh(ResolvedModelSelection model, string mode, bool interactivePermissions)
    {
        var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
        var claim = new WorkingDirectoryClaim(paths.State, hostKey);
        var admission = claim.CreateFresh(workspace.LaunchDirectory, UserSessionId.Generate());
        return Open(model, mode, interactivePermissions, workspace, admission).Session;
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

    private OpenedSession Open(
        ResolvedModelSelection model,
        string mode,
        bool interactivePermissions,
        ProjectWorkspace workspace,
        AdmissionResult admission)
    {
        var id = admission.SessionId
            ?? throw new SessionAdmissionException(admission);
        var activation = admission.ActivationLease
            ?? throw new SessionAdmissionException(admission);
        try
        {
            var resources = new UserSessionResources(paths, id, workspace);
            var index = new SessionIndex(resources);
            var existing = index.Find();
            if (existing is null && File.Exists(resources.MetadataPath))
            {
                throw new InvalidOperationException($"Session metadata for '{id}' is invalid.");
            }

            var rootAgentName = string.IsNullOrEmpty(existing?.RootAgentName) ? "main" : existing.RootAgentName;
            var selected = existing is null ? model : router.Resolve(StoredSelector(existing));
            var selectedMode = existing is null ? mode
                : string.IsNullOrEmpty(existing.Mode) ? modes.Default : existing.Mode;
            var lease = (SessionResourceLease?)SessionResourceLease.Open(resources, activation);
            try
            {
                var session = userSessions.Create(
                    lease ?? throw new InvalidOperationException("The session resource lease was not acquired."),
                    id.Value,
                    rootAgentName,
                    selected,
                    selectedMode,
                    interactivePermissions);
                index.Publish(new SessionMeta
                {
                    Id = id.Value,
                    WorkingDirectory = existing?.WorkingDirectory ?? workspace.LaunchDirectory,
                    RootAgentName = rootAgentName,
                    ProviderId = session.ProviderId,
                    Model = session.CanonicalModel,
                    Selector = session.Model,
                    Mode = session.Mode.Id,
                    LastOpenedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                    CreatedAt = existing?.CreatedAt
                        ?? DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                });
                lease = null;
                return new OpenedSession(session, existing is not null);
            }
            finally
            {
                lease?.Dispose();
            }
        }
        catch
        {
            activation.Dispose();
            throw;
        }
    }
}

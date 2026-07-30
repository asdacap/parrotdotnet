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

    public UserSession Open(ResolvedModelSelection model) => Open(model, modes.Default).Session;

    public OpenedSession Open(ResolvedModelSelection model, string mode)
    {
        var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
        var claim = new WorkingDirectoryClaim(paths.State, hostKey);
        var admission = claim.OpenDefault(workspace.LaunchDirectory);
        return Open(model, mode, workspace, admission);
    }

    public UserSession CreateFresh(ResolvedModelSelection model, string mode)
    {
        var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
        var claim = new WorkingDirectoryClaim(paths.State, hostKey);
        var admission = claim.CreateFresh(workspace.LaunchDirectory, UserSessionId.Generate());
        return Open(model, mode, workspace, admission).Session;
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
        ProjectWorkspace workspace,
        AdmissionResult admission)
    {
        var id = admission.SessionId
            ?? throw new InvalidOperationException($"Cannot open a session: {admission.Disposition}.");
        var activation = admission.ActivationLease
            ?? throw new InvalidOperationException($"Cannot activate session '{id}': {admission.Disposition}.");
        var resources = new UserSessionResources(paths, id, workspace);
        var index = new SessionIndex(resources);
        var existing = index.Find();
        var rootAgentName = string.IsNullOrEmpty(existing?.RootAgentName) ? "main" : existing.RootAgentName;
        var selected = existing is null ? model : router.Resolve(StoredSelector(existing));
        var lease = (SessionResourceLease?)SessionResourceLease.Open(resources, activation);

        try
        {
            if (lease is null)
            {
                throw new InvalidOperationException("The session resource lease was not acquired.");
            }

            var session = userSessions.Create(lease, id.Value, rootAgentName, selected, mode);
            index.Publish(new SessionMeta
            {
                Id = id.Value,
                WorkingDirectory = existing?.WorkingDirectory ?? workspace.LaunchDirectory,
                RootAgentName = rootAgentName,
                ProviderId = session.ProviderId,
                Model = session.CanonicalModel,
                Selector = session.Model,
                Mode = session.Mode.Id,
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
}

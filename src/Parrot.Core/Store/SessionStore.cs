using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.State;

namespace Parrot.Store;

internal sealed class SessionStore(
    StatePaths paths,
    string workingDirectory,
    string hostKey,
    IUserSessionFactory userSessions,
    ModelRouter router,
    ModeRegistry modes,
    DiagnosticLogs diagnostics)
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
                Mode = session.Mode.Profile.Id,
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

    public Task<OpenedSession> Resume(UserSessionId id, bool interactivePermissions)
    {
        var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
        var admission = Acquire(() => new WorkingDirectoryClaim(paths.State, hostKey).Resume(workingDirectory, id));
        return Open(null, modes.Default, interactivePermissions, workspace, admission);
    }

    public async Task<UserSession> Open(ResolvedModelSelection model) =>
        (await Open(model, modes.Default, false).ConfigureAwait(false)).Session;

    public Task<OpenedSession> Open(ResolvedModelSelection model, string mode, bool interactivePermissions)
    {
        var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
        var claim = new WorkingDirectoryClaim(paths.State, hostKey);
        var admission = Acquire(() => claim.OpenDefault(workspace.LaunchDirectory));
        return Open(model, mode, interactivePermissions, workspace, admission);
    }

    public async Task<UserSession> CreateFresh(ResolvedModelSelection model, string mode, bool interactivePermissions)
    {
        var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
        var claim = new WorkingDirectoryClaim(paths.State, hostKey);
        var admission = Acquire(() => claim.CreateFresh(workspace.LaunchDirectory, UserSessionId.Generate()));
        return (await Open(model, mode, interactivePermissions, workspace, admission).ConfigureAwait(false)).Session;
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

    private AdmissionResult Acquire(Func<AdmissionResult> admit)
    {
        try
        {
            var admission = admit();
            return admission.ActivationLease is null ? throw new SessionAdmissionException(admission) : admission;
        }
        catch (Exception failure)
        {
            diagnostics.Global.Write(new DiagnosticEvent("session", "acquire", DiagnosticSeverity.Error)
            {
                Outcome = "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    private async Task<OpenedSession> Open(
        ResolvedModelSelection? model,
        string mode,
        bool interactivePermissions,
        ProjectWorkspace workspace,
        AdmissionResult admission)
    {
        var id = admission.SessionId
            ?? throw new SessionAdmissionException(admission);
        var activation = admission.ActivationLease
            ?? throw new SessionAdmissionException(admission);
        UserSessionResources resources;
        IDiagnosticLog sessionDiagnostics;
        try
        {
            resources = new UserSessionResources(paths, id, workspace);
            sessionDiagnostics = diagnostics.OpenSession(resources);
        }
        catch
        {
            await activation.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var lease = (SessionResourceLease?)SessionResourceLease.Open(resources, activation, sessionDiagnostics);
        UserSession? session = null;
        Exception? openingFailure = null;
        try
        {
            var index = new SessionIndex(resources);
            var existing = index.Find();
            if (existing is null && (model is null || File.Exists(resources.MetadataPath)))
            {
                throw new InvalidOperationException($"Session metadata for '{id}' is unavailable or invalid.");
            }

            var rootAgentName = string.IsNullOrEmpty(existing?.RootAgentName) ? "main" : existing.RootAgentName;
            var selected = existing is null
                ? model ?? throw new InvalidOperationException("A new session requires a model.")
                : router.Resolve(StoredSelector(existing));
            var selectedMode = existing is null ? mode
                : string.IsNullOrEmpty(existing.Mode) ? modes.Default : existing.Mode;
            session = await userSessions.Create(
                lease ?? throw new InvalidOperationException("The session resource lease was not acquired."),
                id.Value,
                rootAgentName,
                selected,
                selectedMode,
                interactivePermissions).ConfigureAwait(false);
            index.Publish(new SessionMeta
            {
                Id = id.Value,
                WorkingDirectory = existing?.WorkingDirectory ?? workspace.LaunchDirectory,
                RootAgentName = rootAgentName,
                ProviderId = session.ProviderId,
                Model = session.CanonicalModel,
                Selector = session.Model,
                Mode = session.Mode.Profile.Id,
                LastOpenedAt = DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
                CreatedAt = existing?.CreatedAt
                    ?? DateTimeOffset.UtcNow.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            });
            lease.Diagnostics.Write(new DiagnosticEvent("session", existing is null ? "start" : "resume", DiagnosticSeverity.Information)
            {
                Outcome = "success",
            });
            lease = null;
            return new OpenedSession(session, existing is not null);
        }
        catch (Exception failure)
        {
            openingFailure = failure;
            sessionDiagnostics.Write(new DiagnosticEvent("session", "open", DiagnosticSeverity.Error)
            {
                Outcome = "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            try
            {
                if (session is not null)
                {
                    await session.DisposeAsync().ConfigureAwait(false);
                    lease = null;
                }
            }
            catch (Exception cleanupFailure)
            {
                sessionDiagnostics.Write(new DiagnosticEvent("session", "cleanup", DiagnosticSeverity.Error)
                {
                    Outcome = "failed",
                    ErrorCode = DiagnosticEvent.ClassifyFailure(cleanupFailure),
                });
            }

            throw;
        }
        finally
        {
            try
            {
                await (lease?.DisposeAsync().AsTask() ?? Task.CompletedTask).ConfigureAwait(false);
            }
            catch (Exception) when (openingFailure is not null)
            {
            }
        }
    }
}

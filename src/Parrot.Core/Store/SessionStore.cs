using System.Diagnostics;
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
        var workspace = ResolveWorkspace(id);
        var admission = Acquire(() => new WorkingDirectoryClaim(paths.State, hostKey).Resume(workingDirectory, id), id);
        return Open(null, modes.Default, interactivePermissions, workspace, admission);
    }

    public async Task<UserSession> Open(ResolvedModelSelection model) =>
        (await Open(model, modes.Default, false).ConfigureAwait(false)).Session;

    public Task<OpenedSession> Open(ResolvedModelSelection model, string mode, bool interactivePermissions)
    {
        var workspace = ResolveWorkspace(null);
        var claim = new WorkingDirectoryClaim(paths.State, hostKey);
        var admission = Acquire(() => claim.OpenDefault(workspace.LaunchDirectory), null);
        return Open(model, mode, interactivePermissions, workspace, admission);
    }

    public async Task<UserSession> CreateFresh(ResolvedModelSelection model, string mode, bool interactivePermissions)
    {
        var workspace = ResolveWorkspace(null);
        var claim = new WorkingDirectoryClaim(paths.State, hostKey);
        var id = UserSessionId.Generate();
        var admission = Acquire(() => claim.CreateFresh(workspace.LaunchDirectory, id), id);
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

    private ProjectWorkspace ResolveWorkspace(UserSessionId? id)
    {
        var started = Stopwatch.GetTimestamp();
        var operation = new DiagnosticEvent("session", "workspace_start", DiagnosticSeverity.Information)
        {
            UserSessionId = id?.Value,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        diagnostics.Global.Write(operation);
        try
        {
            var workspace = ProjectWorkspace.FromLaunchDirectory(workingDirectory);
            diagnostics.Global.Write(operation with
            {
                Operation = "workspace_complete",
                Outcome = "success",
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            return workspace;
        }
        catch (Exception failure)
        {
            diagnostics.Global.Write(operation with
            {
                Operation = "workspace_failure",
                Severity = DiagnosticSeverity.Error,
                Outcome = "failed",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            });
            throw;
        }
    }

    private AdmissionResult Acquire(Func<AdmissionResult> admit, UserSessionId? id)
    {
        var started = Stopwatch.GetTimestamp();
        var operation = new DiagnosticEvent("session", "acquire", DiagnosticSeverity.Information)
        {
            CorrelationId = Guid.NewGuid().ToString("N"),
            UserSessionId = id?.Value,
        };
        diagnostics.Global.Write(operation with { Operation = "acquire_start" });
        try
        {
            var admission = admit();
            if (admission.ActivationLease is null)
            {
                throw new SessionAdmissionException(admission);
            }

            diagnostics.Global.Write(operation with
            {
                Operation = "acquire_complete",
                UserSessionId = admission.SessionId?.Value,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "success",
            });
            return admission;
        }
        catch (Exception failure)
        {
            diagnostics.Global.Write(operation with
            {
                Severity = DiagnosticSeverity.Error,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
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
        var operation = new DiagnosticEvent("session", "resources", DiagnosticSeverity.Information)
        {
            UserSessionId = id.Value,
            CorrelationId = Guid.NewGuid().ToString("N"),
        };
        var started = Stopwatch.GetTimestamp();
        WriteStage("start", null);
        UserSessionResources resources;
        IDiagnosticLog sessionDiagnostics;
        try
        {
            resources = new UserSessionResources(paths, id, workspace);
            sessionDiagnostics = diagnostics.OpenSession(resources);
            WriteStage("complete", null);
        }
        catch (Exception failure)
        {
            WriteStage("failure", failure);
            await activation.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        operation = operation with { Operation = "database" };
        started = Stopwatch.GetTimestamp();
        WriteStage("start", null);
        SessionResourceLease? lease;
        try
        {
            lease = SessionResourceLease.Open(resources, activation, sessionDiagnostics);
            WriteStage("complete", null);
        }
        catch (Exception failure)
        {
            WriteStage("failure", failure);
            throw;
        }

        operation = operation with { Operation = "metadata_read" };
        started = Stopwatch.GetTimestamp();
        WriteStage("start", null);
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
            WriteStage("complete", null);
            operation = operation with { Operation = "initialize" };
            started = Stopwatch.GetTimestamp();
            WriteStage("start", null);
            session = await userSessions.Create(
                lease ?? throw new InvalidOperationException("The session resource lease was not acquired."),
                id.Value,
                rootAgentName,
                selected,
                selectedMode,
                interactivePermissions).ConfigureAwait(false);
            WriteStage("complete", null);
            operation = operation with { Operation = "metadata_publish" };
            started = Stopwatch.GetTimestamp();
            WriteStage("start", null);
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
            WriteStage("complete", null);
            lease.Diagnostics.Write(new DiagnosticEvent("session", existing is null ? "start" : "resume", DiagnosticSeverity.Information)
            {
                Outcome = "success",
            });
            lease = null;
            return new OpenedSession(session, existing is not null);
        }
        catch (Exception failure)
        {
            WriteStage("failure", failure);
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

        void WriteStage(string phase, Exception? failure) => diagnostics.Global.Write(operation with
        {
            Operation = $"{operation.Operation}_{phase}",
            Severity = failure is null or OperationCanceledException ? DiagnosticSeverity.Information : DiagnosticSeverity.Error,
            DurationMilliseconds = phase == "start" ? null : (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
            Outcome = phase == "start" ? null : failure is null ? "success" : failure is OperationCanceledException ? "cancelled" : "failed",
            ErrorCode = failure is null ? null : DiagnosticEvent.ClassifyFailure(failure),
        });
    }
}

using System.Diagnostics;
using Grpc.Core;
using Parrot.Diagnostics;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class LocalChatStartup(
    StatePaths paths,
    string workingDirectory,
    string hostKey,
    TextWriter error,
    IDiagnosticLog diagnostics,
    Func<CancellationToken, Task<GeneratedParrot.ParrotClient>> openLocalClient,
    Func<GeneratedParrot.ParrotClient, CancellationToken, Task<CreateSessionRequest>> configureFresh) : IDisposable
{
    private GrpcTransportClient? _connection;

    public async Task<(GeneratedParrot.ParrotClient Client, UserSession Session)> Open(
        bool interactivePermissions,
        CancellationToken cancellationToken)
    {
        var startupId = FileDiagnosticLog.CreateInstanceId();
        var started = Stopwatch.GetTimestamp();
        diagnostics.Write(new DiagnosticEvent("transport", "local.open.start", DiagnosticSeverity.Information)
        {
            CorrelationId = startupId,
        });
        try
        {
            var opened = await OpenSession(interactivePermissions, cancellationToken).ConfigureAwait(false);
            diagnostics.Write(new DiagnosticEvent("transport", "local.open.complete", DiagnosticSeverity.Information)
            {
                CorrelationId = startupId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "success",
            });
            return opened;
        }
        catch (Exception failure)
        {
            var cancelled = failure is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled };
            diagnostics.Write(new DiagnosticEvent(
                "transport", "local.open.complete", cancelled ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                CorrelationId = startupId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = cancelled ? "cancelled" : "failure",
                ErrorCode = failure is RpcException rpcFailure
                    ? rpcFailure.StatusCode.ToString() : DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    public void Dispose() => _connection?.Dispose();

    private async Task<(GeneratedParrot.ParrotClient Client, UserSession Session)> OpenSession(
        bool interactivePermissions, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var candidate = new WorkingDirectoryClaim(paths.State, hostKey).DiscoverLatest(workingDirectory);
        GeneratedParrot.ParrotClient? localClient = null;
        if (candidate.Disposition == ClaimDisposition.Corrupt)
        {
            throw new InvalidOperationException("cannot open the workspace's latest user session: corrupt state");
        }

        if (candidate.SessionId is { } sessionId)
        {
            UserSession? attached = null;
            try
            {
                attached = await Attach(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is RpcException or TimeoutException or InvalidOperationException
                or IOException or System.Net.Sockets.SocketException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                localClient = await openLocalClient(cancellationToken).ConfigureAwait(false);
                try
                {
                    var loaded = await Resume(localClient, sessionId, interactivePermissions, cancellationToken)
                        .ConfigureAwait(false);
                    return (localClient, loaded);
                }
                catch (RpcException resumeFailure) when (resumeFailure.StatusCode == StatusCode.AlreadyExists)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                try
                {
                    attached = await Attach(sessionId, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception retryFailure) when (retryFailure is RpcException or TimeoutException or InvalidOperationException
                    or IOException or System.Net.Sockets.SocketException)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await error.WriteLineAsync(
                        $"parrot: unable to connect to existing user session {sessionId}: {retryFailure.Message}; creating a new user session".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (attached is not null)
            {
                await error.WriteLineAsync(
                    $"parrot: connected to existing user session {attached.Id}".AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                return ((_connection ?? throw new InvalidOperationException("the attached connection was closed")).Client, attached);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        localClient ??= await openLocalClient(cancellationToken).ConfigureAwait(false);
        var request = await configureFresh(localClient, cancellationToken).ConfigureAwait(false);
        request.InteractivePermissions = interactivePermissions;
        var created = await localClient.CreateSessionAsync(request, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (localClient, created);
    }

    private async Task<UserSession> Attach(UserSessionId sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var resources = new UserSessionResources(paths, sessionId, ProjectWorkspace.FromLaunchDirectory(workingDirectory));
        try
        {
            _connection = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{resources.SocketPath}"), null, diagnostics);
            return await _connection.Attach(
                new AttachSessionRequest
                {
                    UserSessionId = sessionId.Value,
                    WorkingDirectory = workingDirectory,
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            _connection?.Dispose();
            _connection = null;
            throw;
        }
    }

    private async Task<UserSession> Resume(
        GeneratedParrot.ParrotClient localClient,
        UserSessionId sessionId,
        bool interactivePermissions,
        CancellationToken cancellationToken)
    {
        var loaded = await localClient.ResumeSessionAsync(
            new ResumeSessionRequest
            {
                UserSessionId = sessionId.Value,
                WorkingDirectory = workingDirectory,
                InteractivePermissions = interactivePermissions,
            },
            cancellationToken: cancellationToken).ConfigureAwait(false);
        await error.WriteLineAsync(
            $"parrot: loaded existing user session {loaded.Id}".AsMemory(), cancellationToken).ConfigureAwait(false);
        return loaded;
    }
}

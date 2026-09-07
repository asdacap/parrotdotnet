using Grpc.Core;
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
    Func<CancellationToken, Task<GeneratedParrot.ParrotClient>> openLocalClient,
    Func<GeneratedParrot.ParrotClient, CancellationToken, Task<CreateSessionRequest>> configureFresh) : IDisposable
{
    private GrpcTransportClient? _connection;

    public async Task<(GeneratedParrot.ParrotClient Client, UserSession Session)> Open(
        bool interactivePermissions,
        CancellationToken cancellationToken)
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
            if (candidate.Disposition == ClaimDisposition.Resumed)
            {
                localClient = await openLocalClient(cancellationToken).ConfigureAwait(false);
                try
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
                        $"parrot: loaded existing user session {loaded.Id}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    return (localClient, loaded);
                }
                catch (RpcException failure) when (failure.StatusCode == StatusCode.AlreadyExists)
                {
                }
            }

            var resources = new UserSessionResources(paths, sessionId, ProjectWorkspace.FromLaunchDirectory(workingDirectory));
            UserSession? attached = null;
            try
            {
                _connection = GrpcTransportClient.Connect(TransportAddress.Parse($"unix:{resources.SocketPath}"), null);
                attached = await _connection.Attach(
                    new AttachSessionRequest
                    {
                        UserSessionId = sessionId.Value,
                        WorkingDirectory = workingDirectory,
                    },
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception failure) when (failure is RpcException or TimeoutException or InvalidOperationException
                or IOException or System.Net.Sockets.SocketException)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _connection?.Dispose();
                _connection = null;
                await error.WriteLineAsync(
                    $"parrot: unable to connect to existing user session {sessionId}: {failure.Message}; creating a new user session".AsMemory(),
                    cancellationToken).ConfigureAwait(false);
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

    public void Dispose() => _connection?.Dispose();
}

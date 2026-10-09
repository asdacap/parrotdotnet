using System.Net.Sockets;
using Grpc.Core;
using Parrot.Diagnostics;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class LocalTerminalSessionNavigation(
    StatePaths paths,
    string workingDirectory,
    IDiagnosticLog diagnostics,
    Func<CancellationToken, Task<CallInvoker>> openLocalClient) : ITerminalSessionNavigation
{
    public async Task<ListSessionsResponse> List(UserSession current, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        var invoker = await openLocalClient(cancellationToken).ConfigureAwait(false);
        var client = new GeneratedParrot.ParrotClient(invoker);
        return await client.ListSessionsAsync(
            new ListSessionsRequest { WorkingDirectory = workingDirectory },
            cancellationToken: cancellationToken).ConfigureAwait(false);
    }

    public async Task<TerminalSessionTarget> Open(
        UserSession current, string userSessionId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await Attach(userSessionId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure) when (CanResume(failure))
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        var invoker = await openLocalClient(cancellationToken).ConfigureAwait(false);
        var client = new GeneratedParrot.ParrotClient(invoker);
        try
        {
            var selected = await client.ResumeSessionAsync(
                new ResumeSessionRequest
                {
                    UserSessionId = userSessionId,
                    WorkingDirectory = workingDirectory,
                    InteractivePermissions = true,
                    TakeOver = true,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            Validate(selected, userSessionId);
            return new TerminalSessionTarget(invoker, selected, null);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.AlreadyExists)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return await Attach(userSessionId, cancellationToken).ConfigureAwait(false);
        }
    }

    public void Dispose()
    {
    }

    private static bool CanResume(Exception failure) => failure switch
    {
        RpcException rpc => rpc.StatusCode is StatusCode.Unavailable or StatusCode.NotFound or StatusCode.DeadlineExceeded,
        IOException or SocketException or TimeoutException => true,
        _ => false,
    };

    private async Task<TerminalSessionTarget> Attach(string userSessionId, CancellationToken cancellationToken)
    {
        var resources = new UserSessionResources(
            paths, UserSessionId.Parse(userSessionId), ProjectWorkspace.FromLaunchDirectory(workingDirectory));
        var connection = GrpcTransportClient.Connect(
            TransportAddress.Parse($"unix:{resources.SocketPath}"), null, diagnostics);
        try
        {
            var selected = await connection.Attach(
                new AttachSessionRequest { UserSessionId = userSessionId, WorkingDirectory = workingDirectory },
                cancellationToken).ConfigureAwait(false);
            Validate(selected, userSessionId);
            var target = new TerminalSessionTarget(connection.Invoker, selected, connection);
            connection = null;
            return target;
        }
        finally
        {
            connection?.Dispose();
        }
    }

    private void Validate(UserSession selected, string userSessionId)
    {
        if (!string.Equals(selected.Id, userSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the server returned a different user session");
        }

        if (selected.WorkingDirectory.Length > 0
            && !ProjectWorkspace.FromLaunchDirectory(workingDirectory)
                .Equals(ProjectWorkspace.FromLaunchDirectory(selected.WorkingDirectory)))
        {
            throw new InvalidOperationException("the user session belongs to another workspace");
        }
    }
}

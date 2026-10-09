using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class ExplicitRemoteTerminalSessionNavigation(
    CallInvoker invoker) : ITerminalSessionNavigation
{
    private readonly GeneratedParrot.ParrotClient _client = new(invoker);

    public async Task<ListSessionsResponse> List(UserSession current, CancellationToken cancellationToken) =>
        await _client.ListSessionsAsync(
            new ListSessionsRequest { WorkingDirectory = RequireWorkspace(current) },
            cancellationToken: cancellationToken).ConfigureAwait(false);

    public async Task<TerminalSessionTarget> Open(
        UserSession current, string userSessionId, CancellationToken cancellationToken)
    {
        var workspace = RequireWorkspace(current);
        UserSession selected;
        try
        {
            selected = await _client.AttachSessionAsync(
                new AttachSessionRequest { UserSessionId = userSessionId, WorkingDirectory = workspace },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
        {
            selected = await _client.ResumeSessionAsync(
                new ResumeSessionRequest
                {
                    UserSessionId = userSessionId,
                    WorkingDirectory = workspace,
                    InteractivePermissions = true,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (!string.Equals(selected.Id, userSessionId, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("the server returned a different user session");
        }

        return new TerminalSessionTarget(invoker, selected, null);
    }

    public void Dispose()
    {
    }

    private static string RequireWorkspace(UserSession current)
    {
        ArgumentNullException.ThrowIfNull(current);
        if (string.IsNullOrWhiteSpace(current.WorkingDirectory))
        {
            throw new InvalidOperationException("workspace session selection is unavailable on this server: the session workspace is missing");
        }

        return current.WorkingDirectory;
    }
}

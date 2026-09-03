using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class SessionsCommand(
    GeneratedParrot.ParrotClient client,
    ISlashSession session,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/sessions";

    public string Summary => "List sessions available on this server";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        ListSessionsResponse listed;

        try
        {
            listed = await client.ListSessionsAsync(
                new ListSessionsRequest(), cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.Unimplemented)
        {
            await dialog.ShowError("session listing is unavailable on this server", cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await dialog.Show(
            [.. listed.Sessions
                .OrderBy(item => item.CreatedAt, StringComparer.Ordinal)
                .ThenBy(item => item.UserSessionId, StringComparer.Ordinal)
                .Select(item =>
                    $"{(string.Equals(item.UserSessionId, session.Id, StringComparison.Ordinal) ? "*" : " ")} " +
                    $"{(item.RootAgentName.Length == 0 ? "<unnamed>" : item.RootAgentName)}  " +
                    $"{item.UserSessionId}  {(item.Model.Length == 0 ? "<unknown>" : item.Model)}  " +
                    $"{State(item.State)}")],
            cancellationToken).ConfigureAwait(false);
    }

    private static string State(SessionState state) => state switch
    {
        SessionState.Active => "active",
        SessionState.Inactive => "inactive",
        SessionState.Corrupt => "corrupt",
        _ => "unknown",
    };
}

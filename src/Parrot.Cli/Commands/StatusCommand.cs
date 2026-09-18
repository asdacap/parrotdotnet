using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class StatusCommand(
    GeneratedParrot.ParrotClient client,
    ISlashSession session,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/status";

    public string Summary => "Show the agent status prompt and subscription usage";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        SessionStatusResponse status;

        try
        {
            status = await dialog.Load(
                "Loading status…",
                async token => await client.SessionStatusAsync(
                    new SessionStatusRequest { UserSessionId = session.Id },
                    cancellationToken: token),
                cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode is StatusCode.Unimplemented or StatusCode.NotFound)
        {
            await dialog.ShowError(
                failure.StatusCode == StatusCode.Unimplemented
                    ? "session status is unavailable on this server"
                    : "no status is available yet for this session",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var lines = new List<string>();
        if (status.Status.Length > 0)
        {
            lines.Add(status.Status);
        }

        lines.AddRange(status.UsageLines);
        if (lines.Count == 0)
        {
            lines.Add("no status is currently available");
        }

        await dialog.Show(lines, cancellationToken).ConfigureAwait(false);
    }
}

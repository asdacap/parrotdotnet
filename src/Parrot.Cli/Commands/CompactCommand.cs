namespace Parrot.Cli.Commands;

internal sealed class CompactCommand(
    ISlashSession session,
    ISlashActivity activity,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/compact";

    public string Summary => "Compact the current session";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length > 0)
        {
            await dialog.ShowError("usage: /compact", cancellationToken).ConfigureAwait(false);
            return;
        }

        await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
        await session.Compact(cancellationToken).ConfigureAwait(false);
    }
}

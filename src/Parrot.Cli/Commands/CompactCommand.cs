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

        // A failed compaction is already reported as a CompactionFailed event by
        // the session; swallowing it here keeps the process up for the next prompt.
        try
        {
            await session.Compact(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await dialog.ShowError($"compaction failed: {failure.Message}", cancellationToken).ConfigureAwait(false);
        }
    }
}

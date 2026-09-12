using Parrot.Context;

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
        string? target;
        try
        {
            target = ParseTarget(arguments);
        }
        catch (FormatException)
        {
            await dialog.ShowError($"usage: {Name}", cancellationToken).ConfigureAwait(false);
            return;
        }

        await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);

        try
        {
            await session.Compact(target, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await dialog.ShowError($"compaction failed: {failure.Message}", cancellationToken).ConfigureAwait(false);
        }
    }

    private static string? ParseTarget(string arguments)
    {
        var trimmed = arguments.Trim();
        if (trimmed.Length == 0)
        {
            if (arguments.Length > 0)
            {
                throw new FormatException("expected one context size");
            }

            return null;
        }

        if (trimmed.Contains(' ')
            || trimmed.Contains('\t')
            || trimmed.Contains('\r')
            || trimmed.Contains('\n'))
        {
            throw new FormatException("expected one context size");
        }

        _ = ContextSize.Parse(trimmed);
        return trimmed;
    }
}

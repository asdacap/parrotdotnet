using Parrot.Context;

namespace Parrot.Cli.Commands;

internal sealed class SetContextLimitCommand(
    ISlashSession session,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/set-context-limit";

    public string Summary => "Set the automatic context compaction trigger";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        var value = arguments.Trim();
        try
        {
            if (value.Length == 0
                || value.Contains(' ')
                || value.Contains('\t')
                || value.Contains('\r')
                || value.Contains('\n'))
            {
                throw new FormatException("expected one context size");
            }

            _ = ContextSize.Parse(value);
        }
        catch (FormatException)
        {
            await dialog.ShowError($"usage: {Name} <size>", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var response = await session.SetContextLimit(value, cancellationToken).ConfigureAwait(false);
            var message = response.AliasOverride
                ? $"Context compaction limit set to {value}; the current model alias override remains effective"
                : $"Context compaction limit set to {value}";
            await dialog.Show([message], cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await dialog.ShowError($"failed to set context limit: {failure.Message}", cancellationToken).ConfigureAwait(false);
        }
    }
}

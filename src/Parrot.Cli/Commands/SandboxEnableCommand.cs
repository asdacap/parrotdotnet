namespace Parrot.Cli.Commands;

internal sealed class SandboxEnableCommand(
    ISlashSession session,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/sandbox_enable";

    public string Summary => "Globally enable or disable the OS process sandbox";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        var value = arguments.Trim();
        if (!bool.TryParse(value, out var enabled))
        {
            await dialog.ShowError($"usage: {Name} <true|false>", cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var response = await session.SandboxEnable(enabled, cancellationToken).ConfigureAwait(false);
            await dialog.Show([response.Enabled ? "sandbox enabled" : "sandbox disabled"], cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            await dialog.ShowError($"failed to set sandbox: {failure.Message}", cancellationToken).ConfigureAwait(false);
        }
    }
}

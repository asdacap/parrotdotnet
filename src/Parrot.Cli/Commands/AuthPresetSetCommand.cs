using Parrot.Auth;
using Parrot.Config;

namespace Parrot.Cli.Commands;

internal sealed class AuthPresetSetCommand(
    CredentialPresets presets,
    ICredentialStore credentials,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/auth-preset-set";

    public string Summary => "Save all stored credentials as a named preset";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            Configuration.ValidatePresetName(arguments);
        }
        catch (InvalidDataException)
        {
            await dialog.ShowError(
                $"usage: {Name} <name>; name must be one token without whitespace or '/'",
                cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var names = await credentials.List(cancellationToken).ConfigureAwait(false);
            if (names.Count == 0)
            {
                await dialog.ShowError("no credentials are stored", cancellationToken).ConfigureAwait(false);
                return;
            }

            await presets.Save(arguments, credentials, cancellationToken).ConfigureAwait(false);
            await dialog.Show(
                [$"Credential preset saved: {arguments} ({string.Join(", ", names)})"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (AuthException failure)
        {
            await dialog.ShowError(failure.Message, cancellationToken).ConfigureAwait(false);
        }
    }
}

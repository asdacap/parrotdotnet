using Parrot.Auth;
using Parrot.Config;

namespace Parrot.Cli.Commands;

internal sealed class AuthPresetSelectCommand(
    CredentialPresets presets,
    ICredentialStore credentials,
    ISlashActivity activity,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/auth-preset-select";

    public string Summary => "Replace all stored credentials with a saved preset";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (string.IsNullOrWhiteSpace(arguments))
            {
                await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
                var picked = await PickPreset(cancellationToken).ConfigureAwait(false);
                if (picked is null)
                {
                    return;
                }

                arguments = picked;
            }
            else
            {
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

                var names = await presets.List(cancellationToken).ConfigureAwait(false);
                if (!names.Contains(arguments, StringComparer.Ordinal))
                {
                    throw new AuthException("auth: credential preset not found");
                }

                await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
            }

            if (!await PreserveCurrent(arguments, cancellationToken).ConfigureAwait(false))
            {
                return;
            }

            await presets.Apply(arguments, credentials, cancellationToken).ConfigureAwait(false);
            await dialog.Show([$"Credential preset selected: {arguments}"], cancellationToken).ConfigureAwait(false);
        }
        catch (AuthException failure)
        {
            await dialog.ShowError(failure.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    // Syncs refreshed tokens back into the matching preset, or asks before
    // discarding credentials that no preset holds. False when the user declines.
    private async Task<bool> PreserveCurrent(string target, CancellationToken cancellationToken)
    {
        var matched = await presets.Match(credentials, cancellationToken).ConfigureAwait(false);
        if (matched is not null)
        {
            await presets.Save(matched, credentials, cancellationToken).ConfigureAwait(false);
            return true;
        }

        var names = await credentials.List(cancellationToken).ConfigureAwait(false);
        return names.Count == 0 || await dialog.Confirm(
            [
                $"Stored credentials ({string.Join(", ", names)}) do not match any saved preset.",
                $"Selecting {target} replaces them; save them first with /auth-preset-set. Continue?",
            ],
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> PickPreset(CancellationToken cancellationToken)
    {
        var names = await dialog.Load(
            "Loading presets…",
            async token => await presets.List(token).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);

        if (names.Count == 0)
        {
            await dialog.ShowError("no credential presets are saved", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var chosen = await dialog.Select(
            "Select a credential preset",
            [.. names.Select(name => new SlashDialogOption(name, name, string.Empty))],
            cancellationToken).ConfigureAwait(false);
        return chosen?.Id;
    }
}

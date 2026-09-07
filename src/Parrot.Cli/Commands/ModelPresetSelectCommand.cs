using Grpc.Core;
using Parrot.Config;

namespace Parrot.Cli.Commands;

internal sealed class ModelPresetSelectCommand(
    ISlashSession session,
    ISlashActivity activity,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/model-preset-select";

    public string Summary => "Select a saved model selection preset";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

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

            await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            var preset = await session.SelectModelPreset(arguments, cancellationToken).ConfigureAwait(false);
            await dialog.Show(
                [$"Model preset selected: {preset.Name} = {preset.Model}"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure)
        {
            await dialog.ShowError(failure.Status.Detail, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<string?> PickPreset(CancellationToken cancellationToken)
    {
        var presets = await dialog.Load(
            "Loading presets…",
            session.ListModelPresets,
            cancellationToken).ConfigureAwait(false);

        if (presets.Count == 0)
        {
            await dialog.ShowError("no model presets are saved", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var chosen = await dialog.Select(
            "Select a model preset",
            [.. presets.Select(preset => new SlashDialogOption(preset.Name, preset.Name, preset.Model))],
            cancellationToken).ConfigureAwait(false);
        return chosen?.Id;
    }
}

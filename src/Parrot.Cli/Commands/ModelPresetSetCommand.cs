using Grpc.Core;
using Parrot.Config;

namespace Parrot.Cli.Commands;

internal sealed class ModelPresetSetCommand(
    ISlashSession session,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/model-preset-set";

    public string Summary => "Save the current model selection and aliases as a preset";

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
            var preset = await session.SetModelPreset(arguments, cancellationToken).ConfigureAwait(false);
            await dialog.Show(
                [$"Model preset saved: {preset.Name} = {preset.Model}"],
                cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure)
        {
            await dialog.ShowError(failure.Status.Detail, cancellationToken).ConfigureAwait(false);
        }
    }
}

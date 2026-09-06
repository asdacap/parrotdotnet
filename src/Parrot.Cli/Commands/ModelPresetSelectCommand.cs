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
            await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
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
}

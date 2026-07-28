namespace Parrot.Cli.Commands;

internal sealed class ModelCommand(
    ModelWizard selection,
    ISlashSession session,
    ISlashActivity activity,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/model";

    public string Summary => "Switch the model for this session";

    public async Task Run(CancellationToken cancellationToken)
    {
        var selected = await selection.Select(session.Model, cancellationToken).ConfigureAwait(false);
        if (selected is null)
        {
            return;
        }

        await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
        await session.SelectModel(selected, cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"model is now {selected} (saved)"], cancellationToken).ConfigureAwait(false);
    }
}

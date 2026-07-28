namespace Parrot.Cli.Commands;

// A fresh session rather than a wiped one: the old session keeps its history
// and stays listed, which is what upstream's /clear does too.
internal sealed class ClearCommand(
    ModelWizard models,
    ModeSelection modes,
    ISlashSession session,
    ISlashActivity activity,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/clear";

    public string Summary => "Start a fresh session, keeping the old one";

    public async Task Run(CancellationToken cancellationToken)
    {
        var model = await models.Select(null, cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return;
        }

        var mode = await modes.Select(cancellationToken).ConfigureAwait(false);
        if (mode is null)
        {
            return;
        }

        await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
        await session.StartNew(model, mode, cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"new session {session.Id}"], cancellationToken).ConfigureAwait(false);
    }
}

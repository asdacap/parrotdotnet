namespace Parrot.Cli.Commands;

internal sealed class ModeCommand(
    ModeSelection selection,
    ISlashSession session,
    ISlashActivity activity,
    ISlashDialog dialog,
    Func<CancellationToken, Task> refreshSkillCompletion) : ISlashCommand
{
    public string Name => "/mode";

    public string Summary => "Switch the mode for this session";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        var selected = await selection.Select(cancellationToken).ConfigureAwait(false);
        if (selected is null)
        {
            return;
        }

        await activity.WaitUntilIdle(cancellationToken).ConfigureAwait(false);
        await session.SelectMode(selected, cancellationToken).ConfigureAwait(false);
        await refreshSkillCompletion(cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"mode is now {selected}"], cancellationToken).ConfigureAwait(false);
    }
}

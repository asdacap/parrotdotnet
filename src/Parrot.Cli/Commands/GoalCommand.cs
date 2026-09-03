namespace Parrot.Cli.Commands;

internal sealed class GoalCommand(ISlashSession session, ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/goal";

    public string Summary => "Set or clear the session goal";

    public async Task Run(string arguments, CancellationToken cancellationToken)
    {
        if (arguments.Length == 0)
        {
            await session.ClearGoal(cancellationToken).ConfigureAwait(false);
            await dialog.Show(["goal cleared"], cancellationToken).ConfigureAwait(false);
            return;
        }

        await session.SetGoal(arguments, cancellationToken).ConfigureAwait(false);
        await dialog.Show([$"goal set: {arguments}"], cancellationToken).ConfigureAwait(false);
    }
}

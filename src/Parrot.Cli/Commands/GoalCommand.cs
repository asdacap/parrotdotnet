namespace Parrot.Cli.Commands;

internal sealed class GoalCommand(ISlashSession session) : ISlashCommand
{
    public string Name => "/goal";

    public string Summary => "Set or clear the session goal";

    public Task Run(string arguments, CancellationToken cancellationToken) => arguments.Length == 0
        ? session.ClearGoal(cancellationToken)
        : session.SetGoal(arguments, cancellationToken);
}

namespace Parrot.Cli.Commands;

internal sealed class StandingInstructionCommand(ISlashSession session) : ISlashCommand
{
    public string Name => "/standing-instruction";

    public string Summary => "Set or clear the instruction added to every agent's system prompt";

    public Task Run(string arguments, CancellationToken cancellationToken) =>
        session.SetStandingInstruction(arguments, cancellationToken);
}

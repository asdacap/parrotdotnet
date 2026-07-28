namespace Parrot.Cli.Commands;

internal sealed class ExitCommand(IApplicationExit application) : ISlashCommand
{
    public string Name => "/exit";

    public string Summary => "Leave the session";

    public Task Run(CancellationToken cancellationToken) => application.Exit(cancellationToken);
}

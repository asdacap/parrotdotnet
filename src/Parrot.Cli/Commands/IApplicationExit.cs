namespace Parrot.Cli.Commands;

internal interface IApplicationExit
{
    Task Exit(CancellationToken cancellationToken);
}

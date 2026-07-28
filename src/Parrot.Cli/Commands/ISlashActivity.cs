namespace Parrot.Cli.Commands;

internal interface ISlashActivity
{
    Task WaitUntilIdle(CancellationToken cancellationToken);
}

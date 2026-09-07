namespace Parrot.Cli.Commands;

/// <summary>Lets commands wait for active session work to become idle.</summary>
internal interface ISlashActivity
{
    Task WaitUntilIdle(CancellationToken cancellationToken);
}

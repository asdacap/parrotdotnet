namespace Parrot.Cli.Commands;

/// <summary>Handles application exit requests from commands.</summary>
internal interface IApplicationExit
{
    Task Exit(CancellationToken cancellationToken);
}

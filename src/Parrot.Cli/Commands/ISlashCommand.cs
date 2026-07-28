namespace Parrot.Cli.Commands;

internal interface ISlashCommand
{
    string Name { get; }

    string Summary { get; }

    Task Run(CancellationToken cancellationToken);
}

namespace Parrot.Cli.Commands;

internal interface ISlashCommand
{
    string Name { get; }

    string Summary { get; }

    Task Run(string arguments, CancellationToken cancellationToken);
}

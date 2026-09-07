namespace Parrot.Cli.Commands;

/// <summary>Handles one named slash command and supplies its help summary.</summary>
internal interface ISlashCommand
{
    string Name { get; }

    string Summary { get; }

    /// <summary>Executes the command with the argument text following its name.</summary>
    Task Run(string arguments, CancellationToken cancellationToken);
}

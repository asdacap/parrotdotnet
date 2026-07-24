namespace Parrot.Cli.Commands;

// One type per command. Adding /compact at M4 adds a file and touches nothing
// else, which is the Open-Closed principle doing the work a switch would not.
internal interface ISlashCommand
{
    string Name { get; }

    string Summary { get; }

    Task<SlashOutcome> Run(SlashContext context, string arguments, CancellationToken cancellationToken);
}

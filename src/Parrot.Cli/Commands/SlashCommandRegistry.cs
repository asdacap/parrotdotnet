namespace Parrot.Cli.Commands;

internal sealed class SlashCommandRegistry(IReadOnlyList<ISlashCommand> commands)
{
    public IReadOnlyList<ISlashCommand> Commands { get; } = commands;

    public ISlashCommand? Find(string name) =>
        Commands.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.Ordinal));
}

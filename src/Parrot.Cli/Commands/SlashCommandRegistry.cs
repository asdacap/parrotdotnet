namespace Parrot.Cli.Commands;

internal sealed class SlashCommandRegistry(IReadOnlyList<ISlashCommand> commands, ISlashDialog dialog)
{
    public IReadOnlyList<ISlashCommand> Commands { get; } = commands;

    public ISlashCommand? Find(string name) =>
        Commands.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.Ordinal));

    public IReadOnlyList<ISlashCommand> Complete(string entered)
    {
        ArgumentNullException.ThrowIfNull(entered);

        var name = Name(entered);
        return name.StartsWith('/')
            ? [.. Commands
                .Where(command => command.Name.StartsWith(name, StringComparison.Ordinal))
                .OrderBy(command => command.Name, StringComparer.Ordinal)]
            : [];
    }

    public async Task Dispatch(string entered, CancellationToken cancellationToken)
    {
        var name = Name(entered);
        var command = Find(name);

        if (command is null)
        {
            await dialog.ShowError($"unknown command {name}, try /help", cancellationToken).ConfigureAwait(false);
            return;
        }

        await command.Run(cancellationToken).ConfigureAwait(false);
    }

    private static string Name(string entered)
    {
        var end = entered.IndexOfAny([' ', '\t', '\r', '\n']);
        return end < 0 ? entered : entered[..end];
    }
}

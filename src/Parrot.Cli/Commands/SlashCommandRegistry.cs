namespace Parrot.Cli.Commands;

internal sealed class SlashCommandRegistry(IReadOnlyList<ISlashCommand> commands, ISlashDialog dialog)
{
    public IReadOnlyList<ISlashCommand> Commands { get; } = commands;

    public ISlashCommand? Find(string name) =>
        Commands.FirstOrDefault(command => string.Equals(command.Name, name, StringComparison.Ordinal));

    public async Task Dispatch(string entered, CancellationToken cancellationToken)
    {
        var end = entered.IndexOfAny([' ', '\t', '\r', '\n']);
        var name = end < 0 ? entered : entered[..end];
        var command = Find(name);

        if (command is null)
        {
            await dialog.ShowError($"unknown command {name}, try /help", cancellationToken).ConfigureAwait(false);
            return;
        }

        await command.Run(cancellationToken).ConfigureAwait(false);
    }
}

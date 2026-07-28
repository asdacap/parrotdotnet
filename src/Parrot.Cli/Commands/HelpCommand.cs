namespace Parrot.Cli.Commands;

internal sealed class HelpCommand(SlashCommandRegistry registry, ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/help";

    public string Summary => "List the commands";

    public Task Run(CancellationToken cancellationToken) =>
        dialog.Show(
            [.. registry.Commands
                .OrderBy(command => command.Name, StringComparer.Ordinal)
                .Select(command => $"{command.Name,-12} {command.Summary}")],
            cancellationToken);
}

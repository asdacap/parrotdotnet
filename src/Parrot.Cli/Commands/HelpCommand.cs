namespace Parrot.Cli.Commands;

// The registry is passed in rather than looked up, so /help cannot fall out of
// date with what is actually registered.
internal sealed class HelpCommand(SlashCommandRegistry registry) : ISlashCommand
{
    public string Name => "/help";

    public string Summary => "List the commands";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        foreach (var command in registry.Commands.OrderBy(item => item.Name, StringComparer.Ordinal))
        {
            await context.Output
                .WriteLineAsync($"  {command.Name,-12} {command.Summary}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        return SlashOutcome.Continue;
    }
}

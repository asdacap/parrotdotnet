namespace Parrot.Cli.Commands;

internal sealed class ExitCommand : ISlashCommand
{
    public string Name => "/exit";

    public string Summary => "Leave the session";

    public Task<SlashOutcome> Run(SlashContext context, string arguments, CancellationToken cancellationToken) =>
        Task.FromResult(SlashOutcome.Exit);
}

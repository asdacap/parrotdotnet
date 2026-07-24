namespace Parrot.Cli.Commands;

internal sealed class VersionCommand : ISlashCommand
{
    public string Name => "/version";

    public string Summary => "Print the build version";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        await context.Output
            .WriteLineAsync($"{BuildInfo.ProductName} {BuildInfo.Version}".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        return SlashOutcome.Continue;
    }
}

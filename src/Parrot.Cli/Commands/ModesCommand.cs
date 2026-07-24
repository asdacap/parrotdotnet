using Parrot.Protocol;

namespace Parrot.Cli.Commands;

internal sealed class ModesCommand : ISlashCommand
{
    public string Name => "/modes";

    public string Summary => "List the available modes";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listed = await context.Client
            .ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);

        foreach (var mode in listed.Modes)
        {
            await context.Output.WriteLineAsync($"  {mode.Id}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        return SlashOutcome.Continue;
    }
}

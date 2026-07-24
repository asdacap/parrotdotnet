using Parrot.Protocol;

namespace Parrot.Cli.Commands;

internal sealed class ModelsCommand : ISlashCommand
{
    public string Name => "/models";

    public string Summary => "List the models the provider serves";

    public async Task<SlashOutcome> Run(
        SlashContext context, string arguments, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        var listed = await context.Client
            .ListModelsAsync(new ListModelsRequest(), cancellationToken: cancellationToken);

        foreach (var model in listed.Models)
        {
            await context.Output
                .WriteLineAsync($"  {model.ProviderId}/{model.Id}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        return SlashOutcome.Continue;
    }
}

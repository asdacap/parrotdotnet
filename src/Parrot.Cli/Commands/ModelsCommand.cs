using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class ModelsCommand(GeneratedParrot.ParrotClient client, ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/models";

    public string Summary => "List the models the provider serves";

    public async Task Run(CancellationToken cancellationToken)
    {
        var listed = await client.ListModelsAsync(new ListModelsRequest(), cancellationToken: cancellationToken);
        var lines = listed.Models.Select(model => $"{model.ProviderId}/{model.Id}").ToList();

        if (lines.Count == 0)
        {
            lines.Add("no providers are configured");
        }

        await dialog.Show(lines, cancellationToken).ConfigureAwait(false);
    }
}

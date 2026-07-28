using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class ModelAliasCommand(
    GeneratedParrot.ParrotClient client,
    ModelWizard models,
    ISlashDialog dialog) : ISlashCommand
{
    public string Name => "/model-alias";

    public string Summary => "Configure a model alias";

    public async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            var listed = await client.ListModelAliasesAsync(
                new ListModelAliasesRequest(), cancellationToken: cancellationToken);
            if (listed.Aliases.Count == 0)
            {
                await dialog.ShowError("no model aliases configured", cancellationToken).ConfigureAwait(false);
                return;
            }

            var selectedAlias = await dialog.Select(
                "Select a model alias",
                [.. listed.Aliases
                    .OrderBy(alias => alias.Name, StringComparer.Ordinal)
                    .Select(alias => new SlashDialogOption(
                        alias.Name,
                        alias.Name,
                        $"{alias.Usage}; {(alias.ModelString.Length == 0 ? "not configured" : alias.ModelString)}"))],
                cancellationToken).ConfigureAwait(false);
            if (selectedAlias is null)
            {
                return;
            }

            var target = await models.SelectExplicitEffort(cancellationToken).ConfigureAwait(false);
            if (target is null)
            {
                return;
            }

            _ = await client.ConfigureModelAliasAsync(
                new ConfigureModelAliasRequest { Name = selectedAlias.Id, ModelString = target },
                cancellationToken: cancellationToken);
            await dialog.Show(
                [$"Model alias configured: {selectedAlias.Id} = {target}"], cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure)
        {
            await dialog.ShowError(failure.Status.Detail, cancellationToken).ConfigureAwait(false);
        }
    }
}

using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class ModelAliasCommand(
    GeneratedParrot.ParrotClient client,
    ModelWizard models,
    ISlashDialog dialog) : ISlashCommand
{
    private const string ProviderDefaultsId = "/provider-defaults";

    public string Name => "/model-alias";

    public string Summary => "Configure a model alias";

    public async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            var listed = await client.ListModelAliasesAsync(
                new ListModelAliasesRequest(), cancellationToken: cancellationToken);
            var options = listed.Aliases
                .OrderBy(alias => alias.Name, StringComparer.Ordinal)
                .Select(alias => new SlashDialogOption(
                    alias.Name,
                    alias.Name,
                    $"{alias.Usage}; {(alias.ModelString.Length == 0 ? "not configured" : alias.ModelString)}"))
                .Prepend(new SlashDialogOption(
                    ProviderDefaultsId,
                    "Use provider defaults",
                    "Configure all four model aliases"))
                .ToArray();

            var selectedAlias = await dialog.Select(
                "Select a model alias",
                options,
                cancellationToken).ConfigureAwait(false);
            if (selectedAlias is null)
            {
                return;
            }

            if (string.Equals(selectedAlias.Id, ProviderDefaultsId, StringComparison.Ordinal))
            {
                var defaults = await client.ListProviderModelAliasDefaultsAsync(
                    new ListProviderModelAliasDefaultsRequest(), cancellationToken: cancellationToken);
                if (defaults.Providers.Count == 0)
                {
                    await dialog.ShowError(
                        "no available providers have model alias defaults", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var provider = await dialog.Select(
                    "Select provider defaults",
                    [.. defaults.Providers
                        .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
                        .Select(item => new SlashDialogOption(item.ProviderId, item.ProviderId, string.Empty))],
                    cancellationToken).ConfigureAwait(false);
                if (provider is null)
                {
                    return;
                }

                _ = await client.ApplyProviderModelAliasDefaultsAsync(
                    new ApplyProviderModelAliasDefaultsRequest { ProviderId = provider.Id },
                    cancellationToken: cancellationToken);
                await dialog.Show(
                    [$"Model aliases configured from {provider.Id} defaults"], cancellationToken).ConfigureAwait(false);
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

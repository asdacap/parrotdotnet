using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Commands;

internal sealed class ModelWizard(GeneratedParrot.ParrotClient client, ISlashDialog dialog)
{
    public Task<string?> Select(string? currentSelector, CancellationToken cancellationToken) =>
        Select(currentSelector, false, cancellationToken);

    public Task<string?> SelectExplicitEffort(CancellationToken cancellationToken) =>
        Select(null, true, cancellationToken);

    private async Task<string?> Select(
        string? currentSelector,
        bool selectEffort,
        CancellationToken cancellationToken)
    {
        var listed = await client.ListModelsAsync(new ListModelsRequest(), cancellationToken: cancellationToken);
        var providers = listed.Models
            .Select(model => model.ProviderId)
            .Distinct(StringComparer.Ordinal)
            .Select(provider => new SlashDialogOption(provider, provider, "Model provider"))
            .ToArray();

        if (providers.Length == 0)
        {
            await dialog.ShowError("no providers are configured", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var provider = await dialog.Select("Select a provider", providers, cancellationToken).ConfigureAwait(false);
        if (provider is null)
        {
            return null;
        }

        var providerModels = listed.Models
            .Where(model => string.Equals(model.ProviderId, provider.Id, StringComparison.Ordinal))
            .ToArray();
        var model = await dialog.Select(
            "Select a model",
            [.. providerModels.Select(item => new SlashDialogOption(item.Id, item.Id, provider.Id))],
            cancellationToken).ConfigureAwait(false);
        if (model is null)
        {
            return null;
        }

        var selected = new ModelSelection(
            providerModels.Single(item => string.Equals(item.Id, model.Id, StringComparison.Ordinal)),
            null);
        if (selectEffort && selected.Model.Variants.Count > 0)
        {
            var effort = await dialog.Select(
                "Select model effort",
                [.. selected.Model.Variants.Select(variant =>
                    new SlashDialogOption(variant.Name, variant.Name, variant.ReasoningEffort))],
                cancellationToken).ConfigureAwait(false);
            return effort is null ? null : selected.WithVariant(effort.Id)?.Selector;
        }

        var canonicalCurrent = currentSelector is null
            ? null
            : await ModelAliasSelection.Resolve(client, currentSelector, cancellationToken).ConfigureAwait(false);
        var current = canonicalCurrent is null ? null : ModelSelection.Resolve(listed.Models, canonicalCurrent);
        selected = current?.Variant is { } currentVariant
            ? selected.WithVariant(currentVariant.Name) ?? selected.WithFirstVariant()
            : selected.WithFirstVariant();
        return selected.Selector;
    }
}

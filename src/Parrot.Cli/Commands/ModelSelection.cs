using Parrot.Protocol;

namespace Parrot.Cli.Commands;

internal sealed class ModelSelection(Model model, ModelVariant? variant)
{
    public Model Model { get; } = model;

    public ModelVariant? Variant { get; } = variant;

    public string BaseSelector => $"{Model.ProviderId}/{Model.Id}";

    public string Selector => Variant is null ? BaseSelector : $"{BaseSelector}/{Variant.Name}";

    public static ModelSelection? Resolve(IEnumerable<Model> models, string selector)
    {
        ArgumentNullException.ThrowIfNull(models);

        var separator = selector.IndexOf('/');
        if (separator <= 0 || separator == selector.Length - 1
            || selector.Split('/').Any(part => part.Length == 0))
        {
            return null;
        }

        var providerId = selector[..separator];
        var remainder = selector[(separator + 1)..];
        var providerModels = models.Where(
            model => string.Equals(model.ProviderId, providerId, StringComparison.Ordinal)).ToList();
        var exact = providerModels.Where(
            model => string.Equals(model.Id, remainder, StringComparison.Ordinal)).ToList();

        if (exact.Count == 1)
        {
            return new(exact[0], null);
        }

        if (exact.Count > 1)
        {
            return null;
        }

        var candidates = providerModels
            .SelectMany(model => model.Variants
                .Where(variant => string.Equals(
                    remainder, $"{model.Id}/{variant.Name}", StringComparison.Ordinal))
                .Select(variant => new ModelSelection(model, variant)))
            .ToList();

        return candidates.Count == 1 ? candidates[0] : null;
    }

    public ModelSelection? WithVariant(string name)
    {
        var matches = Model.Variants.Where(
            variant => string.Equals(variant.Name, name, StringComparison.Ordinal)).ToList();
        return matches.Count == 1 ? new(Model, matches[0]) : null;
    }

    public ModelSelection WithFirstVariant() =>
        Model.Variants.Count == 0 ? this : new(Model, Model.Variants[0]);
}

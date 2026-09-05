namespace Parrot.Llm;

// Builds a provider's model catalogue from what the endpoint serves. Endpoint
// metadata has priority; declarations and then preset defaults fill fields the
// endpoint omitted. Declared models are always kept, while defaults disappear
// after a successful endpoint response omits them. A null fetched list means no
// catalogue has loaded yet, and configured models stand in for one.
internal static class ModelCatalogue
{
    public static IReadOnlyList<LLMModel> Merge(
        IReadOnlyList<LLMModel>? fetched,
        IReadOnlyList<LLMModel> declared,
        IReadOnlyList<LLMModel> defaults)
    {
        var configured = new Dictionary<string, LLMModel>(StringComparer.Ordinal);

        foreach (var item in defaults)
        {
            configured[item.Id] = item;
        }

        foreach (var item in declared)
        {
            configured[item.Id] = configured.TryGetValue(item.Id, out var fallback)
                ? Overlay(fallback, item)
                : item;
        }

        var source = fetched ?? [.. configured.Values];
        var result = new List<LLMModel>();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in source)
        {
            if (!listed.Add(item.Id))
            {
                continue;
            }

            var model = configured.TryGetValue(item.Id, out var fallback)
                ? Overlay(fallback, item)
                : item;

            if (model.Name.Length == 0)
            {
                model = model with { Name = model.Id };
            }

            if (!model.Capabilities.Reasoning && model.Capabilities.Variants.Count > 0)
            {
                model = model with
                {
                    Capabilities = model.Capabilities with { Reasoning = true },
                };
            }

            result.Add(model);
        }

        foreach (var item in declared)
        {
            if (!listed.Contains(item.Id))
            {
                result.Add(configured[item.Id]);
            }
        }

        result.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return result;
    }

    public static IReadOnlyList<LLMModel> Supplement(
        IReadOnlyList<LLMModel> primary,
        IReadOnlyList<LLMModel> supplemental)
    {
        var supplementalById = new Dictionary<string, LLMModel>(StringComparer.Ordinal);

        foreach (var item in supplemental)
        {
            _ = supplementalById.TryAdd(item.Id, item);
        }

        return
        [
            .. primary.Select(model => supplementalById.TryGetValue(model.Id, out var fallback)
                ? Overlay(fallback, model)
                : model),
        ];
    }

    private static LLMModel Overlay(LLMModel fallback, LLMModel preferred)
    {
        var fallbackCapabilities = fallback.Capabilities;
        var preferredCapabilities = preferred.Capabilities;
        var tools = Prefer(ModelMetadataFields.Tools) ? preferredCapabilities.Tools : fallbackCapabilities.Tools;
        var reasoning = Prefer(ModelMetadataFields.Reasoning)
            ? preferredCapabilities.Reasoning
            : fallbackCapabilities.Reasoning;
        var output = Prefer(ModelMetadataFields.Output) ? preferredCapabilities.Output : fallbackCapabilities.Output;
        var variants = Prefer(ModelMetadataFields.Variants)
            ? preferredCapabilities.Variants
            : fallbackCapabilities.Variants;

        return preferred with
        {
            ProviderId = preferred.ProviderId.Length > 0 ? preferred.ProviderId : fallback.ProviderId,
            Name = Prefer(ModelMetadataFields.Name) ? preferred.Name : fallback.Name,
            ContextWindow = Prefer(ModelMetadataFields.ContextWindow) ? preferred.ContextWindow : fallback.ContextWindow,
            MaxOutputTokens = Prefer(ModelMetadataFields.MaxOutputTokens)
                ? preferred.MaxOutputTokens
                : fallback.MaxOutputTokens,
            InputPrice = Prefer(ModelMetadataFields.InputPrice) ? preferred.InputPrice : fallback.InputPrice,
            CachedInputPrice = Prefer(ModelMetadataFields.CachedInputPrice)
                ? preferred.CachedInputPrice
                : fallback.CachedInputPrice,
            OutputPrice = Prefer(ModelMetadataFields.OutputPrice) ? preferred.OutputPrice : fallback.OutputPrice,
            Capabilities = new ModelCapabilities(tools, reasoning, output, variants),
            Fields = fallback.Fields | preferred.Fields,
        };

        bool Prefer(ModelMetadataFields field) =>
            preferred.Fields.HasFlag(field) || !fallback.Fields.HasFlag(field);
    }
}

namespace Parrot.Llm;

// Builds a provider's model catalogue from what the endpoint serves, described
// with the best metadata available: a declared model first, then a preset
// default. Declared models are always kept; defaults are only descriptions and
// are dropped when the endpoint does not serve them. A null fetched list means
// no catalogue has loaded yet, and the defaults stand in for one. Port of Go's
// mergeModels.
internal static class ModelCatalogue
{
    public static IReadOnlyList<LLMModel> Merge(
        IReadOnlyList<LLMModel>? fetched,
        IReadOnlyList<LLMModel> declared,
        IReadOnlyList<LLMModel> defaults)
    {
        var describe = new Dictionary<string, LLMModel>(StringComparer.Ordinal);

        foreach (var item in defaults)
        {
            describe[item.Id] = item;
        }

        foreach (var item in declared)
        {
            describe[item.Id] = item;
        }

        var source = fetched
            ?? [.. describe.Keys.Select(id => new LLMModel(id, describe[id].ProviderId))];

        var result = new List<LLMModel>();
        var listed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in source)
        {
            if (!listed.Add(item.Id))
            {
                continue;
            }

            var model = describe.TryGetValue(item.Id, out var described) ? Overlay(described, item) : item;

            if (model.Name.Length == 0)
            {
                model = model with { Name = model.Id };
            }

            var capabilities = model.Capabilities;

            if (!capabilities.Reasoning && capabilities.Variants.Count > 0)
            {
                model = model with { Capabilities = capabilities with { Reasoning = true } };
            }

            result.Add(model);
        }

        foreach (var item in declared)
        {
            if (!listed.Contains(item.Id))
            {
                result.Add(item);
            }
        }

        result.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return result;
    }

    // Known metadata wins; the catalogue only fills the gaps the declaration or
    // preset left open.
    private static LLMModel Overlay(LLMModel known, LLMModel fetched)
    {
        var capabilities = known.Capabilities;

        if (capabilities.Variants.Count == 0 && fetched.Capabilities.Variants.Count > 0)
        {
            capabilities = capabilities with { Variants = fetched.Capabilities.Variants };
        }

        return known with
        {
            ProviderId = known.ProviderId.Length > 0 ? known.ProviderId : fetched.ProviderId,
            Name = known.Name.Length > 0 ? known.Name : fetched.Name,
            ContextWindow = known.ContextWindow != 0 ? known.ContextWindow : fetched.ContextWindow,
            MaxOutputTokens = known.MaxOutputTokens != 0 ? known.MaxOutputTokens : fetched.MaxOutputTokens,
            InputPrice = known.InputPrice != 0 ? known.InputPrice : fetched.InputPrice,
            OutputPrice = known.OutputPrice != 0 ? known.OutputPrice : fetched.OutputPrice,
            Capabilities = capabilities,
        };
    }
}

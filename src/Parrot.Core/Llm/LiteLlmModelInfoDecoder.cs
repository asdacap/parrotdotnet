using System.Text.Json;

namespace Parrot.Llm;

internal sealed class LiteLlmModelInfoDecoder : IModelListDecoder
{
    private const ModelMetadataFields SupplementalFields = ModelMetadataFields.ContextWindow
        | ModelMetadataFields.MaxOutputTokens
        | ModelMetadataFields.MaxInputTokens
        | ModelMetadataFields.InputPrice
        | ModelMetadataFields.CachedInputPrice
        | ModelMetadataFields.OutputPrice
        | ModelMetadataFields.Tools
        | ModelMetadataFields.Reasoning
        | ModelMetadataFields.Output
        | ModelMetadataFields.Variants;

    public static LiteLlmModelInfoDecoder Instance { get; } = new();

    public static bool NeedsSupplement(IReadOnlyList<LLMModel> models) =>
        models.Any(model => (model.Fields & SupplementalFields) != SupplementalFields);

    public IReadOnlyList<LLMModel> Decode(string providerId, string json)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object
            || !document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.Array)
        {
            throw new LLMProviderException("provider: model info response does not contain a data array");
        }

        var models = new List<LLMModel>();
        var modelIndexes = new Dictionary<string, int>(StringComparer.Ordinal);
        var conflictingPrices = new Dictionary<string, ModelMetadataFields>(StringComparer.Ordinal);

        foreach (var item in data.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var id = JsonRead.String(item, "model_name");

            if (id.Length == 0)
            {
                continue;
            }

            LLMModel? configured = null;
            LLMModel? normalized = null;

            if (item.TryGetProperty("litellm_params", out var litellmParams)
                && litellmParams.ValueKind == JsonValueKind.Object
                && litellmParams.TryGetProperty("model_info", out var configuredModelInfo)
                && configuredModelInfo.ValueKind == JsonValueKind.Object)
            {
                configured = ModelMetadataDecoder.Decode(
                    id, providerId, id, configuredModelInfo, [], readName: false);
            }

            if (item.TryGetProperty("model_info", out var normalizedModelInfo)
                && normalizedModelInfo.ValueKind == JsonValueKind.Object)
            {
                normalized = ModelMetadataDecoder.Decode(
                    id, providerId, id, normalizedModelInfo, [], readName: false);
            }

            LLMModel decoded;

            if (normalized is not null)
            {
                decoded = configured is null
                    ? normalized
                    : ModelCatalogue.Supplement([normalized], [configured]).Single();
            }
            else if (configured is not null)
            {
                decoded = configured;
            }
            else
            {
                continue;
            }

            if (modelIndexes.TryGetValue(id, out var modelIndex))
            {
                var (model, conflicts) = CombineDeployments(
                    models[modelIndex], decoded, conflictingPrices.GetValueOrDefault(id));
                models[modelIndex] = model;
                conflictingPrices[id] = conflicts;
            }
            else
            {
                modelIndexes[id] = models.Count;
                models.Add(decoded);
            }
        }

        return models;
    }

    private static (LLMModel Model, ModelMetadataFields ConflictingPrices) CombineDeployments(
        LLMModel first,
        LLMModel next,
        ModelMetadataFields conflictingPrices)
    {
        var combined = ModelCatalogue.Supplement([first], [next]).Single();
        var fields = combined.Fields;
        var contextWindow = MinimumExplicit(
            ModelMetadataFields.ContextWindow, first.ContextWindow, next.ContextWindow, combined.ContextWindow);
        var maxOutputTokens = MinimumExplicit(
            ModelMetadataFields.MaxOutputTokens, first.MaxOutputTokens, next.MaxOutputTokens, combined.MaxOutputTokens);
        var maxInputTokens = MinimumExplicit(
            ModelMetadataFields.MaxInputTokens, first.MaxInputTokens, next.MaxInputTokens, combined.MaxInputTokens);
        var inputPrice = CombinePrice(
            ModelMetadataFields.InputPrice, first.InputPrice, next.InputPrice, combined.InputPrice);
        var cachedInputPrice = CombinePrice(
            ModelMetadataFields.CachedInputPrice,
            first.CachedInputPrice,
            next.CachedInputPrice,
            combined.CachedInputPrice);
        var outputPrice = CombinePrice(
            ModelMetadataFields.OutputPrice, first.OutputPrice, next.OutputPrice, combined.OutputPrice);
        var firstCapabilities = first.Capabilities;
        var nextCapabilities = next.Capabilities;
        var capabilities = combined.Capabilities;

        if (HasBoth(ModelMetadataFields.Tools))
        {
            capabilities = capabilities with { Tools = firstCapabilities.Tools && nextCapabilities.Tools };
        }

        if (HasBoth(ModelMetadataFields.Reasoning))
        {
            capabilities = capabilities with { Reasoning = firstCapabilities.Reasoning && nextCapabilities.Reasoning };
        }

        if (HasBoth(ModelMetadataFields.Output))
        {
            var nextOutput = nextCapabilities.Output.ToHashSet(StringComparer.Ordinal);
            capabilities = capabilities with
            {
                Output = [.. firstCapabilities.Output.Where(nextOutput.Contains)],
            };
        }

        if (HasBoth(ModelMetadataFields.Variants))
        {
            var nextVariants = nextCapabilities.Variants.Select(variant => variant.Name)
                .ToHashSet(StringComparer.Ordinal);
            capabilities = capabilities with
            {
                Variants = [.. firstCapabilities.Variants.Where(variant => nextVariants.Contains(variant.Name))],
            };
        }

        if (combined.Fields.HasFlag(ModelMetadataFields.Reasoning) && !capabilities.Reasoning)
        {
            capabilities = capabilities with { Variants = [] };
        }

        return (combined with
        {
            ContextWindow = contextWindow,
            MaxOutputTokens = maxOutputTokens,
            MaxInputTokens = maxInputTokens,
            InputPrice = inputPrice,
            CachedInputPrice = cachedInputPrice,
            OutputPrice = outputPrice,
            Capabilities = capabilities,
            Fields = fields,
        }, conflictingPrices);

        int MinimumExplicit(ModelMetadataFields field, int firstValue, int nextValue, int fallback) =>
            HasBoth(field) ? Math.Min(firstValue, nextValue) : fallback;

        double CombinePrice(ModelMetadataFields field, double firstValue, double nextValue, double fallback)
        {
            if (conflictingPrices.HasFlag(field))
            {
                fields &= ~field;
                return 0;
            }

            if (!HasBoth(field) || firstValue.Equals(nextValue))
            {
                return fallback;
            }

            conflictingPrices |= field;
            fields &= ~field;
            return 0;
        }

        bool HasBoth(ModelMetadataFields field) =>
            first.Fields.HasFlag(field) && next.Fields.HasFlag(field);
    }
}

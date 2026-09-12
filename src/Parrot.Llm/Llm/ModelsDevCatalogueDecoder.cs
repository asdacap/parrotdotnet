using System.Text.Json;

namespace Parrot.Llm;

// Parses the public models.dev provider catalogue. Its data is supplemental
// metadata, not an entitlement list, so malformed individual records are
// skipped while a malformed catalogue root is rejected for the caller to
// handle as an unavailable source.
internal static class ModelsDevCatalogueDecoder
{
    private const double TokensPerMillion = 1_000_000;

    public static IReadOnlyDictionary<string, IReadOnlyList<LLMModel>> Decode(string json)
    {
        using var document = JsonDocument.Parse(json);

        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new LLMProviderException("models.dev: catalogue root must be an object");
        }

        var providers = new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal);

        foreach (var provider in document.RootElement.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object
                || !JsonRead.TryReadString(provider.Value, "id", out var providerId)
                || providerId.Length == 0
                || !provider.Value.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var decoded = DecodeModels(providerId, models);
            _ = providers.TryAdd(providerId, decoded);
        }

        return providers;
    }

    private static List<LLMModel> DecodeModels(string providerId, JsonElement models)
    {
        var result = new List<LLMModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in models.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object
                || !JsonRead.TryReadString(entry.Value, "id", out var id)
                || id.Length == 0
                || !seen.Add(id))
            {
                continue;
            }

            result.Add(DecodeModel(id, providerId, entry.Value));
        }

        return result;
    }

    private static LLMModel DecodeModel(string id, string providerId, JsonElement value)
    {
        var fields = ModelMetadataFields.None;
        var hasName = JsonRead.TryReadString(value, "name", out var name);
        fields |= hasName ? ModelMetadataFields.Name : ModelMetadataFields.None;

        var contextWindow = 0;
        var maxInputTokens = 0;
        var maxOutputTokens = 0;
        if (value.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object)
        {
            fields |= TryReadNonNegativeInt(limit, "context", out contextWindow)
                ? ModelMetadataFields.ContextWindow
                : ModelMetadataFields.None;
            fields |= TryReadNonNegativeInt(limit, "input", out maxInputTokens)
                ? ModelMetadataFields.MaxInputTokens
                : ModelMetadataFields.None;
            fields |= TryReadNonNegativeInt(limit, "output", out maxOutputTokens)
                ? ModelMetadataFields.MaxOutputTokens
                : ModelMetadataFields.None;
        }

        var inputPrice = 0.0;
        var cachedInputPrice = 0.0;
        var outputPrice = 0.0;
        if (value.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object)
        {
            fields |= TryReadPerTokenPrice(cost, "input", out inputPrice)
                ? ModelMetadataFields.InputPrice
                : ModelMetadataFields.None;
            fields |= TryReadPerTokenPrice(cost, "cache_read", out cachedInputPrice)
                ? ModelMetadataFields.CachedInputPrice
                : ModelMetadataFields.None;
            fields |= TryReadPerTokenPrice(cost, "output", out outputPrice)
                ? ModelMetadataFields.OutputPrice
                : ModelMetadataFields.None;
        }

        var hasTools = JsonRead.TryReadBool(value, "tool_call", out var tools);
        var hasReasoning = JsonRead.TryReadBool(value, "reasoning", out var reasoning);
        fields |= hasTools ? ModelMetadataFields.Tools : ModelMetadataFields.None;
        fields |= hasReasoning ? ModelMetadataFields.Reasoning : ModelMetadataFields.None;

        IReadOnlyList<string> output = [];
        if (value.TryGetProperty("modalities", out var modalities) && modalities.ValueKind == JsonValueKind.Object
            && JsonRead.TryReadStringArray(modalities, "output", out output))
        {
            fields |= ModelMetadataFields.Output;
        }

        var variants = ReadVariants(value, out var hasVariants);
        fields |= hasVariants ? ModelMetadataFields.Variants : ModelMetadataFields.None;

        return new LLMModel(id, providerId)
        {
            Name = hasName ? name : id,
            ContextWindow = contextWindow,
            MaxInputTokens = maxInputTokens,
            MaxOutputTokens = maxOutputTokens,
            InputPrice = inputPrice,
            CachedInputPrice = cachedInputPrice,
            OutputPrice = outputPrice,
            Capabilities = new ModelCapabilities(tools, reasoning || variants.Count > 0, output, variants),
            Fields = fields,
        };
    }

    private static IReadOnlyList<ModelVariant> ReadVariants(JsonElement value, out bool hasVariants)
    {
        hasVariants = false;
        if (!value.TryGetProperty("reasoning_options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var efforts = new List<string>();
        foreach (var option in options.EnumerateArray())
        {
            if (option.ValueKind != JsonValueKind.Object || JsonRead.String(option, "type") != "effort"
                || !option.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            hasVariants = true;
            foreach (var rawEffort in values.EnumerateArray())
            {
                if (rawEffort.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var effort = rawEffort.GetString() ?? string.Empty;
                if (IsSupportedEffort(effort) && !efforts.Contains(effort, StringComparer.Ordinal))
                {
                    efforts.Add(effort);
                }
            }
        }

        return [.. efforts.Select(effort => new ModelVariant(effort, effort))];
    }

    private static bool IsSupportedEffort(string value) =>
        value is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max";

    private static bool TryReadNonNegativeInt(JsonElement scope, string name, out int result)
    {
        if (JsonRead.TryReadInt(scope, name, out result) && result >= 0)
        {
            return true;
        }

        result = 0;
        return false;
    }

    private static bool TryReadPerTokenPrice(JsonElement scope, string name, out double result)
    {
        if (JsonRead.TryReadNonNegativeNumber(scope, name, out var perMillion))
        {
            result = perMillion / TokensPerMillion;
            return true;
        }

        result = 0;
        return false;
    }
}

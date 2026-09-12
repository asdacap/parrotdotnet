using System.Text.Json;

namespace Parrot.Llm;

internal static class ModelMetadataDecoder
{
    public static LLMModel Decode(
        string id,
        string providerId,
        string fallbackName,
        JsonElement metadata,
        IReadOnlyList<string> contextFields,
        bool readName)
    {
        var fields = ModelMetadataFields.None;
        var name = string.Empty;
        var hasName = readName && JsonRead.TryReadString(metadata, "name", out name);
        var hasContext = TryReadFirstNonNegativeInt(metadata, contextFields, out var contextWindow);
        var hasMaxTokens = TryReadNonNegativeInt(metadata, "max_output_tokens", out var maxOutputTokens);
        var hasMaxInputTokens = TryReadNonNegativeInt(metadata, "max_input_tokens", out var maxInputTokens);
        var hasInputPrice = JsonRead.TryReadNonNegativeNumber(metadata, "input_cost_per_token", out var inputPrice);
        var hasCachedInputPrice = JsonRead.TryReadNonNegativeNumber(
            metadata, "cache_read_input_token_cost", out var cachedInputPrice);
        var hasOutputPrice = JsonRead.TryReadNonNegativeNumber(metadata, "output_cost_per_token", out var outputPrice);
        var hasTools = JsonRead.TryReadBool(metadata, "supports_function_calling", out var tools);
        var hasReasoning = JsonRead.TryReadBool(metadata, "supports_reasoning", out var reasoning);
        var hasOutput = JsonRead.TryReadStringArray(metadata, "supported_output_modalities", out var output);
        var hasVariants = TryReadEfforts(metadata, out var efforts);
        var defaultEffort = JsonRead.String(metadata, "default_reasoning_effort");

        fields |= hasName ? ModelMetadataFields.Name : ModelMetadataFields.None;
        fields |= hasContext ? ModelMetadataFields.ContextWindow : ModelMetadataFields.None;
        fields |= hasMaxTokens ? ModelMetadataFields.MaxOutputTokens : ModelMetadataFields.None;
        fields |= hasMaxInputTokens ? ModelMetadataFields.MaxInputTokens : ModelMetadataFields.None;
        fields |= hasInputPrice ? ModelMetadataFields.InputPrice : ModelMetadataFields.None;
        fields |= hasCachedInputPrice ? ModelMetadataFields.CachedInputPrice : ModelMetadataFields.None;
        fields |= hasOutputPrice ? ModelMetadataFields.OutputPrice : ModelMetadataFields.None;
        fields |= hasTools ? ModelMetadataFields.Tools : ModelMetadataFields.None;
        fields |= hasReasoning ? ModelMetadataFields.Reasoning : ModelMetadataFields.None;
        fields |= hasOutput ? ModelMetadataFields.Output : ModelMetadataFields.None;
        fields |= hasVariants || (hasReasoning && !reasoning)
            ? ModelMetadataFields.Variants
            : ModelMetadataFields.None;

        var variants = hasVariants ? ReasoningVariants.FromEfforts(efforts, defaultEffort) : [];

        return new LLMModel(id, providerId)
        {
            Name = hasName ? name : fallbackName,
            ContextWindow = contextWindow,
            MaxOutputTokens = maxOutputTokens,
            MaxInputTokens = maxInputTokens,
            InputPrice = inputPrice,
            CachedInputPrice = cachedInputPrice,
            OutputPrice = outputPrice,
            Capabilities = new ModelCapabilities(
                Tools: !hasTools || tools,
                Reasoning: (hasReasoning && reasoning) || variants.Count > 0,
                Output: hasOutput ? output : ["text"],
                Variants: variants),
            Fields = fields,
        };
    }

    private static bool TryReadEfforts(JsonElement metadata, out IReadOnlyList<string> efforts)
    {
        if (!JsonRead.TryReadStringArray(metadata, "supported_reasoning_efforts", out var values)
            && !JsonRead.TryReadStringArray(metadata, "reasoning_effort_levels", out values))
        {
            efforts = [];
            return false;
        }

        efforts = [.. values.Where(IsSupportedEffort).Distinct(StringComparer.Ordinal)];
        return true;
    }

    private static bool IsSupportedEffort(string effort) =>
        effort is "none" or "minimal" or "low" or "medium" or "high" or "xhigh" or "max";

    private static bool TryReadFirstNonNegativeInt(
        JsonElement metadata,
        IReadOnlyList<string> names,
        out int result)
    {
        foreach (var name in names)
        {
            if (TryReadNonNegativeInt(metadata, name, out result))
            {
                return true;
            }
        }

        result = 0;
        return false;
    }

    private static bool TryReadNonNegativeInt(JsonElement metadata, string name, out int result)
    {
        if (JsonRead.TryReadInt(metadata, name, out result) && result >= 0)
        {
            return true;
        }

        result = 0;
        return false;
    }
}

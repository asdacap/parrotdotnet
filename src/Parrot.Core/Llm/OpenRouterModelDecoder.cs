using System.Text.Json;

namespace Parrot.Llm;

// Parses an OpenRouter model list. OpenRouter extends the standard format with a
// pricing object (prompt, completion), a top_provider object
// (max_completion_tokens), and a reasoning object (supported_efforts,
// default_effort) that a plain model list cannot express.
internal sealed class OpenRouterModelDecoder : IModelListDecoder
{
    public static OpenRouterModelDecoder Instance { get; } = new();

    public IReadOnlyList<LLMModel> Decode(string providerId, string json)
    {
        using var document = JsonDocument.Parse(json);
        var models = new List<LLMModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        if (document.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                var id = JsonRead.String(item, "id");

                if (id.Length == 0 || !seen.Add(id))
                {
                    continue;
                }

                var fields = ModelMetadataFields.None;
                var hasName = JsonRead.TryReadString(item, "name", out var name);
                var hasContext = JsonRead.TryReadInt(item, "context_length", out var contextWindow);
                var maxTokens = 0;
                var hasMaxTokens = item.TryGetProperty("top_provider", out var topProvider)
                    && JsonRead.TryReadInt(topProvider, "max_completion_tokens", out maxTokens);
                fields |= hasName ? ModelMetadataFields.Name : ModelMetadataFields.None;
                fields |= hasContext ? ModelMetadataFields.ContextWindow : ModelMetadataFields.None;
                fields |= hasMaxTokens ? ModelMetadataFields.MaxOutputTokens : ModelMetadataFields.None;

                IReadOnlyList<ModelVariant> variants = [];

                if (item.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
                {
                    if (JsonRead.TryReadStringArray(reasoning, "supported_efforts", out var efforts))
                    {
                        variants = ReasoningVariants.FromEfforts(efforts, JsonRead.String(reasoning, "default_effort"));
                        fields |= ModelMetadataFields.Reasoning | ModelMetadataFields.Variants;
                    }
                }

                var inputPrice = 0.0;
                var outputPrice = 0.0;

                if (item.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object)
                {
                    fields |= JsonRead.TryReadNonNegativeNumber(pricing, "prompt", out inputPrice)
                        ? ModelMetadataFields.InputPrice
                        : ModelMetadataFields.None;
                    fields |= JsonRead.TryReadNonNegativeNumber(pricing, "completion", out outputPrice)
                        ? ModelMetadataFields.OutputPrice
                        : ModelMetadataFields.None;
                }

                models.Add(new LLMModel(id, providerId)
                {
                    Name = hasName ? name : id,
                    ContextWindow = contextWindow,
                    MaxOutputTokens = maxTokens,
                    InputPrice = inputPrice,
                    OutputPrice = outputPrice,
                    Capabilities = new ModelCapabilities(
                        Tools: true, Reasoning: variants.Count > 0, Output: ["text"], Variants: variants),
                    Fields = fields,
                });
            }
        }

        return models.Count == 0
            ? throw new LLMProviderException("provider: models response contains no usable models")
            : models;
    }
}

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

                var name = JsonRead.String(item, "name");
                var maxTokens = item.TryGetProperty("top_provider", out var topProvider)
                    ? JsonRead.Int(topProvider, "max_completion_tokens")
                    : 0;

                IReadOnlyList<ModelVariant> variants = [];

                if (item.TryGetProperty("reasoning", out var reasoning) && reasoning.ValueKind == JsonValueKind.Object)
                {
                    variants = ReasoningVariants.FromEfforts(
                        JsonRead.StringArray(reasoning, "supported_efforts"),
                        JsonRead.String(reasoning, "default_effort"));
                }

                var inputPrice = 0.0;
                var outputPrice = 0.0;

                if (item.TryGetProperty("pricing", out var pricing) && pricing.ValueKind == JsonValueKind.Object)
                {
                    inputPrice = JsonRead.Number(pricing, "prompt");
                    outputPrice = JsonRead.Number(pricing, "completion");
                }

                models.Add(new LLMModel(id, providerId)
                {
                    Name = name.Length > 0 ? name : id,
                    ContextWindow = JsonRead.Int(item, "context_length"),
                    MaxOutputTokens = maxTokens,
                    InputPrice = inputPrice,
                    OutputPrice = outputPrice,
                    Capabilities = new ModelCapabilities(
                        Tools: true, Reasoning: variants.Count > 0, Output: ["text"], Variants: variants),
                });
            }
        }

        return models.Count == 0
            ? throw new LLMProviderException("provider: models response contains no usable models")
            : models;
    }
}

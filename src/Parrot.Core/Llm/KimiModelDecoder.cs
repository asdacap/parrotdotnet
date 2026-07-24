using System.Text.Json;

namespace Parrot.Llm;

// Parses a Kimi (Moonshot) model list. The endpoint extends the OpenAI format
// with display_name, supports_reasoning, and a think_efforts object (support,
// valid_efforts, default_effort). Supported thinking efforts become variants so
// an /effort selection works without configuration.
internal sealed class KimiModelDecoder : IModelListDecoder
{
    public static KimiModelDecoder Instance { get; } = new();

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

                var name = JsonRead.String(item, "display_name");
                IReadOnlyList<ModelVariant> variants = [];

                if (item.TryGetProperty("think_efforts", out var thinkEfforts)
                    && thinkEfforts.ValueKind == JsonValueKind.Object
                    && JsonRead.Bool(thinkEfforts, "support"))
                {
                    variants = ReasoningVariants.FromEfforts(
                        JsonRead.StringArray(thinkEfforts, "valid_efforts"),
                        JsonRead.String(thinkEfforts, "default_effort"));
                }

                models.Add(new LLMModel(id, providerId)
                {
                    Name = name.Length > 0 ? name : id,
                    ContextWindow = JsonRead.Int(item, "context_length"),
                    Capabilities = new ModelCapabilities(
                        Tools: true,
                        Reasoning: JsonRead.Bool(item, "supports_reasoning") || variants.Count > 0,
                        Output: ["text"],
                        Variants: variants),
                });
            }
        }

        return models.Count == 0
            ? throw new LLMProviderException("provider: models response contains no usable models")
            : models;
    }
}

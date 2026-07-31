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

                var fields = ModelMetadataFields.None;
                var hasName = JsonRead.TryReadString(item, "display_name", out var name);
                var hasContext = JsonRead.TryReadInt(item, "context_length", out var contextWindow);
                var hasReasoning = JsonRead.TryReadBool(item, "supports_reasoning", out var supportsReasoning);
                fields |= hasName ? ModelMetadataFields.Name : ModelMetadataFields.None;
                fields |= hasContext ? ModelMetadataFields.ContextWindow : ModelMetadataFields.None;
                fields |= hasReasoning ? ModelMetadataFields.Reasoning : ModelMetadataFields.None;
                IReadOnlyList<ModelVariant> variants = [];

                if (item.TryGetProperty("think_efforts", out var thinkEfforts)
                    && thinkEfforts.ValueKind == JsonValueKind.Object
                    && JsonRead.TryReadStringArray(thinkEfforts, "valid_efforts", out var efforts))
                {
                    fields |= ModelMetadataFields.Variants;

                    if (JsonRead.Bool(thinkEfforts, "support"))
                    {
                        variants = ReasoningVariants.FromEfforts(
                            efforts,
                            JsonRead.String(thinkEfforts, "default_effort"));
                    }
                }

                models.Add(new LLMModel(id, providerId)
                {
                    Name = hasName ? name : id,
                    ContextWindow = contextWindow,
                    Capabilities = new ModelCapabilities(
                        Tools: true,
                        Reasoning: supportsReasoning || variants.Count > 0,
                        Output: ["text"],
                        Variants: variants),
                    Fields = fields,
                });
            }
        }

        return models.Count == 0
            ? throw new LLMProviderException("provider: models response contains no usable models")
            : models;
    }
}

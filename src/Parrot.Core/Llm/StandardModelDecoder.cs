using System.Text.Json;

namespace Parrot.Llm;

// Parses a standard OpenAI-format model list. Only the id is standard; the
// remaining fields are vendor extensions used when present.
internal sealed class StandardModelDecoder : IModelListDecoder
{
    public static StandardModelDecoder Instance { get; } = new();

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
                var hasContext = JsonRead.TryReadInt(item, "context_window", out var contextWindow)
                    || JsonRead.TryReadInt(item, "context_length", out contextWindow);
                var hasName = JsonRead.TryReadString(item, "name", out var name);
                var hasMaxTokens = JsonRead.TryReadInt(item, "max_output_tokens", out var maxTokens);
                fields |= hasName ? ModelMetadataFields.Name : ModelMetadataFields.None;
                fields |= hasContext ? ModelMetadataFields.ContextWindow : ModelMetadataFields.None;
                fields |= hasMaxTokens ? ModelMetadataFields.MaxOutputTokens : ModelMetadataFields.None;

                models.Add(new LLMModel(id, providerId)
                {
                    Name = hasName ? name : id,
                    ContextWindow = contextWindow,
                    MaxOutputTokens = maxTokens,
                    Capabilities = new ModelCapabilities(Tools: true, Reasoning: false, Output: ["text"], Variants: []),
                    Fields = fields,
                });
            }
        }

        return models.Count == 0
            ? throw new LLMProviderException("provider: models response contains no usable models")
            : models;
    }
}

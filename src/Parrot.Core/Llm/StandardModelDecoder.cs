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

                var contextWindow = JsonRead.Int(item, "context_window");

                if (contextWindow == 0)
                {
                    contextWindow = JsonRead.Int(item, "context_length");
                }

                var name = JsonRead.String(item, "name");

                models.Add(new LLMModel(id, providerId)
                {
                    Name = name.Length > 0 ? name : id,
                    ContextWindow = contextWindow,
                    MaxOutputTokens = JsonRead.Int(item, "max_output_tokens"),
                    Capabilities = new ModelCapabilities(Tools: true, Reasoning: false, Output: ["text"], Variants: []),
                });
            }
        }

        return models.Count == 0
            ? throw new LLMProviderException("provider: models response contains no usable models")
            : models;
    }
}

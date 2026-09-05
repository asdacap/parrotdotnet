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

                models.Add(ModelMetadataDecoder.Decode(
                    id,
                    providerId,
                    id,
                    item,
                    ["context_window", "context_length", "max_input_tokens"],
                    readName: true));
            }
        }

        return models.Count == 0
            ? throw new LLMProviderException("provider: models response contains no usable models")
            : models;
    }
}

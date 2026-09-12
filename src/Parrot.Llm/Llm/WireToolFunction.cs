using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireToolFunction
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string Description { get; init; } = string.Empty;

    [JsonPropertyName("parameters")]
    public JsonNode? Parameters { get; init; }
}

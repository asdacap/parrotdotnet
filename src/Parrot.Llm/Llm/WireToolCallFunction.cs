using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireToolCallFunction
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("arguments")]
    public string? Arguments { get; init; }
}

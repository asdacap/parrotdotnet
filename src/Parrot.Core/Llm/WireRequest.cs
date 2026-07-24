using System.Text.Json.Serialization;

namespace Parrot.Llm;

public sealed class WireRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = true;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("messages")]
    public List<WireMessage> Messages { get; init; } = [];
}

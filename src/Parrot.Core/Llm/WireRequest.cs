using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireRequest
{
    [JsonPropertyName("model")]
    public string Model { get; init; } = string.Empty;

    [JsonPropertyName("stream")]
    public bool Stream { get; init; } = true;

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; init; }

    [JsonPropertyName("messages")]
    public IReadOnlyList<WireMessage> Messages { get; init; } = [];

    [JsonPropertyName("tools")]
    public IReadOnlyList<WireTool>? Tools { get; init; }
}

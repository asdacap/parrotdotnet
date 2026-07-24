using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; init; } = string.Empty;
}

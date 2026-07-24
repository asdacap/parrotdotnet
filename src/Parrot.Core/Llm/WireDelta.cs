using System.Text.Json.Serialization;

namespace Parrot.Llm;

public sealed class WireDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; init; }
}

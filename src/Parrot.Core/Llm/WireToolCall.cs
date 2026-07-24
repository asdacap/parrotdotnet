using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireToolCall
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    [JsonPropertyName("function")]
    public WireToolCallFunction? Function { get; init; }
}

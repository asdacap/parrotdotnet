using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireDelta
{
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("reasoning_content")]
    public string? ReasoningContent { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<WireToolCall>? ToolCalls { get; init; }
}

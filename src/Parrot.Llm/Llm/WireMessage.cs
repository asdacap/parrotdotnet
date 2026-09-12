using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireMessage
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    // Null rather than empty when a message is only tool calls: some endpoints
    // reject an empty-string content beside tool_calls.
    [JsonPropertyName("content")]
    public string? Content { get; init; }

    [JsonPropertyName("tool_calls")]
    public IReadOnlyList<WireToolCall>? ToolCalls { get; init; }

    [JsonPropertyName("tool_call_id")]
    public string? ToolCallId { get; init; }
}

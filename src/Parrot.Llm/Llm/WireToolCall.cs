using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireToolCall
{
    // Present on a streamed response fragment, omitted when sending an assistant
    // message back -- the API keys request tool_calls by position, not index.
    [JsonPropertyName("index")]
    public int? Index { get; init; }

    [JsonPropertyName("id")]
    public string? Id { get; init; }

    // Required on the request; the model also sends it on the response.
    [JsonPropertyName("type")]
    public string? Type { get; init; }

    [JsonPropertyName("function")]
    public WireToolCallFunction? Function { get; init; }
}

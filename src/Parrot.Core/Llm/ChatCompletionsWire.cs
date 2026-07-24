using System.Text.Json.Serialization;

namespace Parrot.Llm;

// The chat-completions wire shape, confirmed against the live OpenCode Go
// endpoint: SSE "data:" lines carrying choices[0].delta, where content and
// reasoning_content are separate fields.
internal sealed class ChatCompletionsWire
{
    [JsonPropertyName("choices")]
    public IReadOnlyList<WireChoice>? Choices { get; init; }

    [JsonPropertyName("usage")]
    public WireUsage? Usage { get; init; }
}

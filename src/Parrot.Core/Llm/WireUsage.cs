using System.Text.Json.Serialization;

namespace Parrot.Llm;

public sealed class WireUsage
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; init; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; init; }
}

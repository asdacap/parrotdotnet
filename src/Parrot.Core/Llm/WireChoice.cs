using System.Text.Json.Serialization;

namespace Parrot.Llm;

public sealed class WireChoice
{
    [JsonPropertyName("delta")]
    public WireDelta? Delta { get; init; }

    [JsonPropertyName("finish_reason")]
    public string? FinishReason { get; init; }
}

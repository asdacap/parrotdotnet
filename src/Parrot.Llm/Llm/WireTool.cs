using System.Text.Json.Serialization;

namespace Parrot.Llm;

internal sealed class WireTool
{
    [JsonPropertyName("type")]
    public string Type { get; init; } = "function";

    [JsonPropertyName("function")]
    public WireToolFunction Function { get; init; } = new();
}

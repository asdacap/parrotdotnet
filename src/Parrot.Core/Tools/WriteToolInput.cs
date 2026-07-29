using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class WriteToolInput
{
    [JsonPropertyName("path")]
    public string? Path { get; init; }

    [JsonPropertyName("content")]
    public string? Content { get; init; }
}

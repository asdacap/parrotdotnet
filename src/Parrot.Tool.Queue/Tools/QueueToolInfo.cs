using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class QueueToolInfo
{
    [JsonPropertyName("path")]
    public required string Path { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("size")]
    public required int Size { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonPropertyName("closed")]
    public bool? Closed { get; init; }
}

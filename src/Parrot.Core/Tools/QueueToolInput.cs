using System.Text.Json.Serialization;

namespace Parrot.Tools;

internal sealed class QueueToolInput
{
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("enabled")]
    public bool? Enabled { get; init; }

    [JsonPropertyName("items")]
    public string[]? Items { get; init; }

    [JsonPropertyName("direction")]
    public string? Direction { get; init; }

    [JsonPropertyName("count")]
    public int? Count { get; init; }

    [JsonPropertyName("yield_after_ms")]
    public long? YieldAfterMilliseconds { get; init; }
}

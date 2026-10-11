using System.Text.Json.Serialization;

namespace Parrot.Queues;

internal sealed record QueueMetadata
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Description { get; init; }

    [JsonPropertyName("closed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool Closed { get; init; }
}

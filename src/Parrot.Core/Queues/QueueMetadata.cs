using System.Text.Json.Serialization;

namespace Parrot.Queues;

internal sealed record QueueMetadata
{
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("description")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? Description { get; init; }

    [JsonPropertyName("listener_session_ids")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public IReadOnlyList<string>? ListenerSessionIds { get; init; }

    [JsonPropertyName("monitored")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool LegacyMonitored { get; init; }

    [JsonPropertyName("delivery_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? DeliveryId { get; init; }

    [JsonPropertyName("delivery_listener_session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public string? DeliveryListenerSessionId { get; init; }
}

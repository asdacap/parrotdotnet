using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AcceptanceVerdictWire(
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("evidence")] JsonElement Evidence,
    [property: JsonPropertyName("feedback")] JsonElement Feedback,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("context")] JsonElement Context);

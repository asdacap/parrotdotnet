using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskLeafResponseWire(
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("evidence")] JsonElement Evidence,
    [property: JsonPropertyName("feedback")] JsonElement Feedback,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("replacement_result")] string? ReplacementResult);

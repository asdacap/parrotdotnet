using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskWire(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("dependencies")] string[]? Dependencies,
    [property: JsonPropertyName("description")] string? Description,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("acceptance_criteria")] string? AcceptanceCriteria,
    [property: JsonPropertyName("model")] string? Model,
    [property: JsonPropertyName("state")] string? State,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("failure")] string? Failure);

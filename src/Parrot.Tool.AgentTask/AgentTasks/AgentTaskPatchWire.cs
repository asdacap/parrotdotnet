using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskPatchWire(
    [property: JsonPropertyName("description")] JsonElement Description,
    [property: JsonPropertyName("payload")] JsonElement Payload,
    [property: JsonPropertyName("acceptance_criteria")] JsonElement AcceptanceCriteria,
    [property: JsonPropertyName("model")] JsonElement Model);

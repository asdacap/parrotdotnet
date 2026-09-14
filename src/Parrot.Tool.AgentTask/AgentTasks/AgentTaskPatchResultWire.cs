using System.Text.Json;
using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskPatchResultWire(
    [property: JsonPropertyName("description"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Description,
    [property: JsonPropertyName("payload"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] JsonElement? Payload,
    [property: JsonPropertyName("acceptance_criteria"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? AcceptanceCriteria,
    [property: JsonPropertyName("model"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Model);

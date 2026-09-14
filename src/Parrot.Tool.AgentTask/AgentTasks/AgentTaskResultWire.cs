using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskResultWire(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("tasks")] IReadOnlyList<AgentTaskResultNodeWire> Tasks);

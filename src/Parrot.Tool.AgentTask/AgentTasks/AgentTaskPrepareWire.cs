using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskPrepareWire(
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("task_patch")] AgentTaskPatchWire? TaskPatch);

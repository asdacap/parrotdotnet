using System.Text.Json.Serialization;

namespace Parrot.AgentTasks;

internal sealed record AgentTaskResultNodeWire(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("attempt_count")] int AttemptCount,
    [property: JsonPropertyName("context")] string? Context,
    [property: JsonPropertyName("result")] string? Result,
    [property: JsonPropertyName("task_patch")] AgentTaskPatchResultWire? TaskPatch,
    [property: JsonPropertyName("execution")] string? Execution,
    [property: JsonPropertyName("verdict")] string? Verdict,
    [property: JsonPropertyName("evidence")] string? Evidence,
    [property: JsonPropertyName("retry_feedback")] IReadOnlyList<string>? RetryFeedback,
    [property: JsonPropertyName("failure")] string? Failure,
    [property: JsonPropertyName("blocked_by")] IReadOnlyList<string>? BlockedBy,
    [property: JsonPropertyName("tasks")] IReadOnlyList<AgentTaskResultNodeWire>? Tasks);

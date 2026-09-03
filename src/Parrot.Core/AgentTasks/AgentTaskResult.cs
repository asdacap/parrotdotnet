namespace Parrot.AgentTasks;

internal sealed record AgentTaskResult(
    string Name,
    AgentTaskExecutionStatus Status,
    int AttemptCount,
    string? Context,
    string? Result,
    AgentTaskPatch? TaskPatch,
    string? Execution,
    AcceptanceVerdict? Verdict,
    IReadOnlyList<string>? RetryFeedback,
    string? Failure,
    IReadOnlyList<string>? BlockedBy,
    IReadOnlyList<AgentTaskResult>? Tasks);

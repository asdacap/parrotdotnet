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
    IReadOnlyList<AgentTaskResult>? Tasks)
{
    internal static AgentTaskResult CreateFailed(string name, string failure) => new(
        name,
        AgentTaskExecutionStatus.Failed,
        0,
        null,
        null,
        null,
        null,
        null,
        null,
        failure,
        null,
        null);

    internal static AgentTaskResult CreateCompletedFailure(
        string name,
        int attempt,
        AgentTaskPatch? taskPatch,
        string? context,
        string? result,
        string? execution,
        AcceptanceVerdict? verdict,
        IReadOnlyList<string>? retryFeedback,
        IReadOnlyList<AgentTaskResult>? nested,
        string failure) => new(
            name,
            AgentTaskExecutionStatus.Failed,
            attempt,
            context,
            result,
            taskPatch,
            execution,
            verdict,
            retryFeedback,
            failure,
            null,
            nested);
}

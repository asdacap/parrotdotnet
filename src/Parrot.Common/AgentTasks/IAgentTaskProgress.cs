using Parrot.Protocol;

namespace Parrot.AgentTasks;

/// <summary>Maintains and publishes the owning task run's progress tree.</summary>
internal interface IAgentTaskProgress
{
    IReadOnlyList<AgentTaskNodeHandle> Initialize(IReadOnlyList<AgentTask> tasks, CancellationToken cancellationToken);

    /// <summary>Captures the current progress revision without publishing an update.</summary>
    AgentTaskProgressSnapshot CurrentSnapshot();

    /// <summary>Initializes the progress tree if necessary and returns its root handles.</summary>
    IReadOnlyList<AgentTaskNodeHandle> EnsureInitialized(IReadOnlyList<AgentTask> tasks, CancellationToken cancellationToken);

    IReadOnlyList<AgentTaskNodeHandle> GetChildren(AgentTaskNodeHandle handle);

    void MarkRunning(AgentTaskNodeHandle handle, CancellationToken cancellationToken);

    void ReportRetry(string path, int nextAttempt, int maximumAttempts, CancellationToken cancellationToken);

    void MarkTerminal(AgentTaskNodeHandle handle, AgentTaskExecutionStatus status, CancellationToken cancellationToken);

    void MarkBlocked(AgentTaskNodeHandle handle, CancellationToken cancellationToken);

    IReadOnlyList<AgentTaskNodeHandle> ReplaceChildren(AgentTaskNodeHandle handle, AgentTaskPayload payload, CancellationToken cancellationToken);

    IReadOnlyList<AgentTaskNodeHandle> UpdatePreparedTask(AgentTaskNodeHandle handle, string description, AgentTaskPayload? payload, CancellationToken cancellationToken);

    void MarkRemainingCanceled(CancellationToken cancellationToken);

    void MarkRemainingFailed(CancellationToken cancellationToken);
}

namespace Parrot.AgentTasks;

/// <summary>Delivers terminal task-run results to their owning agent session.</summary>
internal interface IAgentTaskRunCompletion
{
    /// <summary>Records the completion and wakes the caller; cancellation stops waiting or delivery, not shared message preparation.</summary>
    Task Deliver(AgentTaskRunTerminal terminal, CancellationToken cancellationToken);

    /// <summary>Records the completion without waking the caller during shutdown, honoring delivery cancellation.</summary>
    Task DeliverDuringShutdown(AgentTaskRunTerminal terminal, CancellationToken cancellationToken);
}

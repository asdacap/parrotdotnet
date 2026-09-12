using Parrot.Statuses;

namespace Parrot.AgentTasks;

/// <summary>Owns the agent's active task runs and settles their completion deliveries during shutdown.</summary>
internal interface IAgentTaskRunCatalog : IAsyncDisposable
{
    /// <summary>Captures currently active task work for status reporting.</summary>
    IReadOnlyList<ActiveWorkObservation> Active();

    void Start(AgentTaskRunRequest request, CancellationToken cancellationToken);

    /// <summary>Captures the active task runs with their current progress.</summary>
    IReadOnlyList<AgentTaskRunSnapshot> Snapshot();

    /// <summary>Stops admission and waits for all owned runs to settle.</summary>
    Task Settle();
}

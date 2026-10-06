using Parrot.Agent;
using Parrot.Store;

namespace Parrot.AgentTasks;

/// <summary>
/// Owns the agent's task graph. Runs every task whose dependencies succeeded on its own retained sub-agent, and
/// notifies the owning agent of task successes, failures, owner updates and graph completion.
/// </summary>
internal interface IAgentTaskService : IAsyncDisposable, IAgentWorkOwner
{
    /// <summary>
    /// Upserts tasks by name and reconciles the graph. Any state is accepted, including on running tasks, which are
    /// force-stopped when the set changes them. The selection and history boundary apply to sub-agents this call spawns.
    /// Throws ArgumentException when the merged graph has a missing dependency or a cycle.
    /// </summary>
    void SetTasks(IReadOnlyList<AgentTask> tasks, AgentTurnSelection selection, HistoryForkBoundary historyBoundary);

    /// <summary>Captures every task with its effective state, in declaration order.</summary>
    IReadOnlyList<AgentTask> Snapshot();
}

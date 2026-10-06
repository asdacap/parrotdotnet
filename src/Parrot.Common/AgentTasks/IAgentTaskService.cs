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
    /// force-stopped when the set changes their execution definition or state. Running visibility-only updates retain
    /// execution. The selection and history boundary apply to sub-agents this call spawns.
    /// Throws ArgumentException when the merged graph has a missing dependency or a cycle.
    /// </summary>
    void SetTasks(IReadOnlyList<AgentTask> tasks, AgentTurnSelection selection, HistoryForkBoundary historyBoundary);

    /// <summary>Captures every task with its effective state, in declaration order.</summary>
    IReadOnlyList<AgentTask> Snapshot();

    /// <summary>Captures a named task's effective state and its worker name when spawned, or null when the task is absent.</summary>
    AgentTaskDetail? CaptureDetail(string name);

    /// <summary>
    /// Applies visibility differences between declarations to matching retained tasks without changing execution state.
    /// Missing tasks and tasks with independently changed execution definitions are left unchanged.
    /// </summary>
    void ApplyVisibilityChanges(IReadOnlyList<AgentTask> previous, IReadOnlyList<AgentTask> incoming);
}

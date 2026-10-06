using Parrot.Agent;
using Parrot.Config;
using Parrot.Statuses;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskActiveWorkBlocker(
    IAgentTaskService agentTasks,
    IPromptTemplateCatalog promptTemplates) : IActiveWorkBlocker
{
    public ActiveWorkBlockerResult? Observe()
    {
        var unresolved = agentTasks.Snapshot()
            .Where(static task => task.State is AgentTaskExecutionStatus.Pending or AgentTaskExecutionStatus.Running or AgentTaskExecutionStatus.Failed)
            .Select(static task => new ActiveWorkObservation($"{task.Name} [{task.State.ToString().ToLowerInvariant()}]", task.Description, ActiveWorkState.Running))
            .ToArray();
        return unresolved.Length == 0
            ? null
            : new ActiveWorkBlockerResult(
                new ActiveWorkSection(
                    promptTemplates.Render("agent-session.active-work-unresolved-agent-tasks-heading", []),
                    unresolved).Format(),
                null);
    }
}

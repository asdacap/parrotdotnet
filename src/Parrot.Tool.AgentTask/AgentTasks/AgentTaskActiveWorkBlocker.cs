using Parrot.Agent;
using Parrot.Config;
using Parrot.Statuses;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskActiveWorkBlocker(
    IAgentTaskRunCatalog agentTasks,
    IPromptTemplateCatalog promptTemplates) : IActiveWorkBlocker
{
    public ActiveWorkBlockerResult? Observe()
    {
        var ownedAgentTasks = agentTasks.Active();
        return ownedAgentTasks.Count == 0
            ? null
            : new ActiveWorkBlockerResult(
                new ActiveWorkSection(
                    promptTemplates.Render("agent-session.active-work-agent-tasks-heading", []),
                    ownedAgentTasks).Format(),
                null);
    }
}

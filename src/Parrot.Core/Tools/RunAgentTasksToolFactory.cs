using Parrot.Agent;
using Parrot.Config;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class RunAgentTasksToolFactory(
    ToolWorkspace workspace,
    ModelRouter router,
    EventBroker eventBroker,
    EventRepository eventRepository,
    AgentTaskConfig agentTasks) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new RunAgentTasksTool(workspace, router, session, eventBroker, eventRepository, agentTasks);
}

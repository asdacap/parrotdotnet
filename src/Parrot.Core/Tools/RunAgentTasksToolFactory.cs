using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class RunAgentTasksToolFactory(
    ToolWorkspace workspace,
    AgentRegistry agents,
    ModelRouter router,
    EventBroker eventBroker,
    EventRepository eventRepository) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new RunAgentTasksTool(workspace, agents, router, session, eventBroker, eventRepository);
}

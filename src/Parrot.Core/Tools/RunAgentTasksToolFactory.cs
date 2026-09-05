using Parrot.Agent;
using Parrot.AgentTasks;
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
    IAgentSessionScope ownerScope,
    AgentTaskRunOwner runs,
    ToolOutputBlobStore outputBlobs,
    PromptTemplateCatalog promptTemplates,
    AgentTaskConfig agentTasks) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new RunAgentTasksTool(
            workspace,
            router,
            ownerScope,
            runs,
            new AgentTaskRunCompletion(session, outputBlobs, promptTemplates),
            eventBroker,
            eventRepository,
            agentTasks);
}

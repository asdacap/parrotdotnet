using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class RunAgentTasksToolFactory(
    ToolWorkspace workspace,
    IModelRouter router,
    IEventBroker eventBroker,
    IEventRepository eventRepository,
    IAgentSessionScope ownerScope,
    IAgentTaskRunCatalog runs,
    ToolOutputBlobStore outputBlobs,
    IPromptTemplateCatalog promptTemplates,
    AgentTaskConfig agentTasks,
    IDiagnosticLog diagnostics) : IToolFactory
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
            agentTasks,
            diagnostics);
}

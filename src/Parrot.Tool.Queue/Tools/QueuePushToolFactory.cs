using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueuePushToolFactory(
    IAgentQueues queues,
    IAgentResolver resolver,
    ToolWorkspace workspace,
    IDiagnosticLog diagnostics,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("queue_push");

    public ITool Create(IAgentSession session) => new QueuePushTool(queues, resolver, workspace, diagnostics);
}

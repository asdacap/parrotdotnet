using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueuePushToolFactory(
    IAgentQueues queues,
    ToolWorkspace workspace,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("queue_push");

    public ITool Create(IAgentSession session) => new QueuePushTool(queues, workspace);
}

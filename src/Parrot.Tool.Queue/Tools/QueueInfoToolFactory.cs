using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueInfoToolFactory(IAgentQueues queues, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("queue_info");

    public ITool Create(IAgentSession session) => new QueueInfoTool(queues);
}

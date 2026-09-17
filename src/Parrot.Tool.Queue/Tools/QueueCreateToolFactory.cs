using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueCreateToolFactory(IAgentQueues queues, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("queue_create");

    public ITool Create(IAgentSession session) => new QueueCreateTool(queues);
}

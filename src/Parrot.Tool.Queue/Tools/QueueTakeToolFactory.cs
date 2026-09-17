using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueTakeToolFactory(IAgentQueues queues, IDiagnosticLog diagnostics, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("queue_take");

    public ITool Create(IAgentSession session) => new QueueTakeTool(queues, diagnostics);
}

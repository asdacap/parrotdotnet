using Parrot.Agent;
using Parrot.Diagnostics;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueTakeToolFactory(IAgentQueues queues, IDiagnosticLog diagnostics) : IToolFactory
{
    public ITool Create(IAgentSession session) => new QueueTakeTool(queues, diagnostics);
}

using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueTakeToolFactory(AgentQueues queues) : IToolFactory
{
    public ITool Create(IAgentSession session) => new QueueTakeTool(queues);
}

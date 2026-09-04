using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueListenToolFactory(AgentQueues queues) : IToolFactory
{
    public ITool Create(IAgentSession session) => new QueueListenTool(queues);
}

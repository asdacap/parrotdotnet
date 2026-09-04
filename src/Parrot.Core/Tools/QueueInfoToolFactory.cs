using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueInfoToolFactory(AgentQueues queues) : IToolFactory
{
    public ITool Create(IAgentSession session) => new QueueInfoTool(queues);
}

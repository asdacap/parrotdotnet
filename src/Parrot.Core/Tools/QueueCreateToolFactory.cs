using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueCreateToolFactory(AgentQueues queues) : IToolFactory
{
    public ITool Create(AgentSession session) => new QueueCreateTool(queues);
}

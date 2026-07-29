using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueListenToolFactory(QueueStore queues) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueueListenTool(queues);
}

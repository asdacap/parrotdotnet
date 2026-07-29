using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueTakeToolFactory(QueueStore queues) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueueTakeTool(queues);
}

using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueInfoToolFactory(QueueStore queues) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueueInfoTool(queues);
}

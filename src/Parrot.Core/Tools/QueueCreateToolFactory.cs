using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueCreateToolFactory(QueueStore queues) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueueCreateTool(queues);
}

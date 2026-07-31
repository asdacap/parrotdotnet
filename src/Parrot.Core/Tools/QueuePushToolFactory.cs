using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueuePushToolFactory : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueuePushTool(session.Queues);
}

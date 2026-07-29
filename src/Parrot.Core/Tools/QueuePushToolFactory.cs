using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueuePushToolFactory(UserSession owner) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueuePushTool(owner.Queues, owner);
}

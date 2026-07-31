using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueueTakeToolFactory : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueueTakeTool(session.Queues);
}

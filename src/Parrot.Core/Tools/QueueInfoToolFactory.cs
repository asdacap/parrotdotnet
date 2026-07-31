using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueueInfoToolFactory : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueueInfoTool(session.Queues);
}

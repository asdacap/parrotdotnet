using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueueCreateToolFactory : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new QueueCreateTool(session.Queues);
}

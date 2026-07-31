using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueueListenToolFactory : IToolFactory
{
    public ITool Create(AgentSession session) => new QueueListenTool(session.Queues);
}

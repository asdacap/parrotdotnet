using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueueCreateToolFactory : IToolFactory
{
    public ITool Create(AgentSession session) => new QueueCreateTool(session.Queues);
}

using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueueTakeToolFactory : IToolFactory
{
    public ITool Create(AgentSession session) => new QueueTakeTool(session.Queues);
}

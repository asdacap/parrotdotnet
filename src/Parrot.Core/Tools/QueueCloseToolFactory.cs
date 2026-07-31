using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueueCloseToolFactory : IToolFactory
{
    public ITool Create(AgentSession session) => new QueueCloseTool(session.Queues);
}

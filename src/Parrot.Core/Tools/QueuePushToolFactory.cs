using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class QueuePushToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session) => new QueuePushTool(session.Queues, workspace);
}

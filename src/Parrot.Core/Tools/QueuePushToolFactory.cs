using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueuePushToolFactory(
    AgentQueues queues,
    ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session) => new QueuePushTool(queues, workspace);
}

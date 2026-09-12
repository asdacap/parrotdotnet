using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueuePushToolFactory(
    IAgentQueues queues,
    ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(IAgentSession session) => new QueuePushTool(queues, workspace);
}

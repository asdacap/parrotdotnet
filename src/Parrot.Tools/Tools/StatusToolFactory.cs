using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class StatusToolFactory(IRuntimeStatus status) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new StatusTool(status, session);
}

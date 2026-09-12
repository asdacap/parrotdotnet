using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class WaitToolFactory(IRuntimeStatus status, TimeProvider timeProvider) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new WaitTool(status, session, timeProvider);
}

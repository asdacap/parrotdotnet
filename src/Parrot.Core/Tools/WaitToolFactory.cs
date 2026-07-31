using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class WaitToolFactory(RuntimeStatus status, TimeProvider timeProvider) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new WaitTool(status, session, timeProvider);
}

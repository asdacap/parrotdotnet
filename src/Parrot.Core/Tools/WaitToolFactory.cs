using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class WaitToolFactory(RuntimeStatus status, TimeProvider timeProvider) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) =>
        new WaitTool(status, session, selection, timeProvider);
}

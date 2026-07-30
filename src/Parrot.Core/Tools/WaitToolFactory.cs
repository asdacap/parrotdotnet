using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WaitToolFactory(TimeProvider timeProvider) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) => new WaitTool(session, timeProvider);
}

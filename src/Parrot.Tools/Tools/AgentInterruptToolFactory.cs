using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentInterruptToolFactory : IToolFactory
{
    public ITool Create(IAgentSession session) => new AgentInterruptTool(session.TurnInterruption);
}

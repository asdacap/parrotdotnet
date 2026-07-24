using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WaitAgentToolFactory(AgentRegistry agents) : IToolFactory
{
    public ITool Create(AgentSession session) => new WaitAgentTool(agents, session);
}

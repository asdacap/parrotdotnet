using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSendToolFactory(AgentRegistry agents) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new AgentSendTool(agents, session);
}

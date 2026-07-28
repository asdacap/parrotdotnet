using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSpawnToolFactory(AgentRegistry agents) : IToolFactory
{
    public ITool Create(AgentSession session, AgentSelection selection) => new AgentSpawnTool(agents, session, selection);
}

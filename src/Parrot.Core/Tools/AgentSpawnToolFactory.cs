using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSpawnToolFactory(UserSession owner) : IToolFactory
{
    public ITool Create(AgentSession session) => new AgentSpawnTool(owner, session);
}

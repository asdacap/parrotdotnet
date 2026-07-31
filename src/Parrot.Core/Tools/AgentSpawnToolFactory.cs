using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class AgentSpawnToolFactory(AgentRegistry agents, ModelRouter router) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new AgentSpawnTool(agents, router, session);
}

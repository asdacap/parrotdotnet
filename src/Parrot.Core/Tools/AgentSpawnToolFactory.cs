using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class AgentSpawnToolFactory(
    IAgentSessionScope ownerScope,
    IModelRouter router) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new AgentSpawnTool(ownerScope, router);
}

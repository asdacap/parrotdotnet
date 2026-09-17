using Parrot.Agent;
using Parrot.Llm;

namespace Parrot.Tools;

internal sealed class AgentSpawnToolFactory(
    IAgentSessionScope ownerScope,
    IModelRouter router,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("agent_spawn");

    public ITool Create(IAgentSession session) =>
        new AgentSpawnTool(ownerScope, router);
}

using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentInterruptToolFactory(IChildRegistry registry, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("agent_interrupt");

    public ITool Create(IAgentSession session) => new AgentInterruptTool(registry);
}

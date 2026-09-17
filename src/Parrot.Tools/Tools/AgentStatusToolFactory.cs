using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentStatusToolFactory(IAgentResolver resolver, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("agent_status");

    public ITool Create(IAgentSession session) => new AgentStatusTool(resolver);
}

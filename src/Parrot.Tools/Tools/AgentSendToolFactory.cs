using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class AgentSendToolFactory(
    AgentIdentity identity,
    IAgentResolver resolver,
    AgentSendConfig config,
    ToolDefinitionCatalog definitions,
    IPromptTemplateCatalog templates) : IToolFactory
{
    public IToolDefinition Definition => config.ToParent
        ? definitions.Describe("agent_send")
        : new AgentSendWithoutParentDefinition(definitions.Describe("agent_send"), templates);

    public ITool Create(IAgentSession session) => new AgentSendTool(identity, resolver, session, config);
}

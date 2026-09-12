using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentStatusToolFactory(
    AgentResolver resolver,
    IAgentParentScope parentScope) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AgentStatusTool(resolver, parentScope);
}

using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentStatusToolFactory(
    AgentResolver resolver,
    AgentSessionParentScope parentScope) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AgentStatusTool(resolver, parentScope);
}

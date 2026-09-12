using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentStatusToolFactory(
    IAgentResolver resolver,
    IAgentParentScope parentScope) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AgentStatusTool(resolver, parentScope);
}

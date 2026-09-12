using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSendToolFactory(
    AgentIdentity identity,
    IAgentResolver resolver) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new AgentSendTool(identity, resolver, session);
}

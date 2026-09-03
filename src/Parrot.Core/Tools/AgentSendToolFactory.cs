using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSendToolFactory(
    AgentIdentity identity,
    AgentResolver resolver) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new AgentSendTool(identity, resolver, session);
}

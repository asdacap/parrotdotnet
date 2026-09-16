using Parrot.Agent;
using Parrot.Config;

namespace Parrot.Tools;

internal sealed class AgentSendToolFactory(
    AgentIdentity identity,
    IAgentResolver resolver,
    AgentSendConfig config) : IToolFactory
{
    public ITool Create(IAgentSession session) => new AgentSendTool(identity, resolver, session, config);
}

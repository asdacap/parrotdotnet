using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentSendToolFactory : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new AgentSendTool(session.ChildRegistry, session);
}

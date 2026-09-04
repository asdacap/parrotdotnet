using Parrot.Agent;
using Parrot.Permissions;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionToolFactory(
    AgentIdentity identity,
    AgentSessionSecurity security,
    PermissionBroker broker) : IToolFactory
{
    public bool Supports(IAgentSession session) => identity.Depth == 0;

    public ITool Create(IAgentSession session) => new RequestWritePermissionTool(identity, security, broker);
}

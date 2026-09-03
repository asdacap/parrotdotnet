using Parrot.Agent;
using Parrot.Permissions;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionToolFactory(
    AgentIdentity identity,
    AgentSessionSecurity security,
    PermissionBroker broker) : IToolFactory
{
    public bool Supports(AgentSession session) => identity.Depth == 0;

    public ITool Create(AgentSession session) => new RequestWritePermissionTool(identity, security, broker);
}

using Parrot.Agent;
using Parrot.Permissions;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionToolFactory(PermissionBroker broker) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) =>
        new RequestWritePermissionTool(broker, session, selection.SecurityProfile);
}

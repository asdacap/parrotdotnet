using Parrot.Agent;
using Parrot.Permissions;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionToolFactory(PermissionBroker broker, ToolWorkspace workspace) : IToolFactory
{
    public bool Supports(AgentSession session) => session.Depth == 0;

    public ITool Create(AgentSession session) =>
        new RequestWritePermissionTool(broker, session, workspace);
}

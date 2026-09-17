using Parrot.Agent;
using Parrot.Permissions;

namespace Parrot.Tools;

internal sealed class RequestWritePermissionToolFactory(
    AgentIdentity identity,
    AgentSessionSecurity security,
    IPermissionBroker broker,
    ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("request_write_permission");

    public bool Supports(IAgentSession session) => identity.Depth == 0;

    public ITool Create(IAgentSession session) => new RequestWritePermissionTool(identity, security, broker);
}

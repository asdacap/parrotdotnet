using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WriteToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new WriteTool(workspace, session.WriteGrants);
}

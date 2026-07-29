using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WriteToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) =>
        new WriteTool(workspace, selection.SecurityProfile);
}

using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class ReadToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) =>
        new ReadTool(workspace, selection.SecurityProfile);
}

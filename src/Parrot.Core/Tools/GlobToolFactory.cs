using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class GlobToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) =>
        new GlobTool(workspace, selection.SecurityProfile);
}

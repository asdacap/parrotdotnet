using Parrot.Agent;

namespace Parrot.Tools.ApplyPatch;

internal sealed class ApplyPatchToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) =>
        new ApplyPatchTool(workspace, selection.SecurityProfile);
}

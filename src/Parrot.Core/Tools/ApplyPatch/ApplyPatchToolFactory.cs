using Parrot.Agent;

namespace Parrot.Tools.ApplyPatch;

internal sealed class ApplyPatchToolFactory(string workingDirectory) : IToolFactory
{
    public ITool Create(AgentSession session, AgentTurnSelection selection) =>
        new ApplyPatchTool(workingDirectory, selection.SecurityProfile);
}

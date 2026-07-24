using Parrot.Agent;

namespace Parrot.Tools.ApplyPatch;

internal sealed class ApplyPatchToolFactory(string workingDirectory) : IToolFactory
{
    public ITool Create(AgentSession session) => new ApplyPatchTool(workingDirectory);
}

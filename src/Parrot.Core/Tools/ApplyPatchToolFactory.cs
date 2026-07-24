using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class ApplyPatchToolFactory(string workingDirectory) : IToolFactory
{
    public ITool Create(AgentSession session) => new ApplyPatchTool(workingDirectory);
}

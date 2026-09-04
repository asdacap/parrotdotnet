using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class GlobToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new GlobTool(workspace);
}

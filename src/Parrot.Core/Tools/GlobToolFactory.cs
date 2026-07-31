using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class GlobToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new GlobTool(workspace);
}

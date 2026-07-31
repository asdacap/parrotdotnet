using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class GrepToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new GrepTool(workspace);
}

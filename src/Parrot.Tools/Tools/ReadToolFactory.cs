using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class ReadToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new ReadTool(workspace);
}

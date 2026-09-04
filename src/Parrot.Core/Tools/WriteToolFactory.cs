using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WriteToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new WriteTool(workspace);
}

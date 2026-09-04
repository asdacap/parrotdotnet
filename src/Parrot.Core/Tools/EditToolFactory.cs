using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class EditToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new EditTool(workspace);
}

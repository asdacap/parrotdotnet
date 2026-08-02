using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class EditToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(AgentSession session) =>
        new EditTool(workspace);
}

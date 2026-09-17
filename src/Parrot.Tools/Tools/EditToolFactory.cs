using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class EditToolFactory(ToolWorkspace workspace, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("edit");

    public ITool Create(IAgentSession session) =>
        new EditTool(workspace);
}

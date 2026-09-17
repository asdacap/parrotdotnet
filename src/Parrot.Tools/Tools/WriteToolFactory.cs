using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class WriteToolFactory(ToolWorkspace workspace, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("write");

    public ITool Create(IAgentSession session) =>
        new WriteTool(workspace);
}

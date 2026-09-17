using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class ReadToolFactory(ToolWorkspace workspace, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("read");

    public ITool Create(IAgentSession session) =>
        new ReadTool(workspace);
}

using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class GlobToolFactory(ToolWorkspace workspace, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("glob");

    public ITool Create(IAgentSession session) =>
        new GlobTool(workspace);
}

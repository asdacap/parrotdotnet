using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class ImageGenerationToolFactory(ToolWorkspace workspace, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("imagegen");

    public ITool Create(IAgentSession session) => new ImageGenerationTool(workspace);
}

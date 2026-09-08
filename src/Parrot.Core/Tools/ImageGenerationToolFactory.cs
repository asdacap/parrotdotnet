using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class ImageGenerationToolFactory(ToolWorkspace workspace) : IToolFactory
{
    public ITool Create(IAgentSession session) => new ImageGenerationTool(workspace);
}

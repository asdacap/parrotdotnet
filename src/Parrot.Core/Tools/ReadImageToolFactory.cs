using Parrot.Agent;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class ReadImageToolFactory(ToolWorkspace workspace, ImageArtifactRepository artifacts) : IToolFactory
{
    public ITool Create(IAgentSession session) =>
        new ReadImageTool(workspace, artifacts);
}

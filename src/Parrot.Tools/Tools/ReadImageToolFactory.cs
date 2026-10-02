using Parrot.Agent;
using Parrot.Config;
using Parrot.Store;

namespace Parrot.Tools;

internal sealed class ReadImageToolFactory(ToolWorkspace workspace, IImageArtifactRepository artifacts, ToolDefinitionCatalog definitions, RequestLimitsConfig requestLimits) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("read_image");

    public ITool Create(IAgentSession session) =>
        new ReadImageTool(workspace, artifacts, requestLimits);
}

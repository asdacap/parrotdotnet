using Parrot.Agent;
using Parrot.Web;

namespace Parrot.Tools;

internal sealed class WebFetchToolFactory(WebFetcher fetcher, ToolDefinitionCatalog definitions) : IToolFactory
{
    public IToolDefinition Definition => definitions.Describe("web_fetch");

    public ITool Create(IAgentSession session) => new WebFetchTool(fetcher);
}

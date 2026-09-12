using Parrot.Agent;
using Parrot.Web;

namespace Parrot.Tools;

internal sealed class WebFetchToolFactory(WebFetcher fetcher) : IToolFactory
{
    public ITool Create(IAgentSession session) => new WebFetchTool(fetcher);
}

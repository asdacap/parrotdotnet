using Parrot.Agent;
using Parrot.Web;

namespace Parrot.Tools;

internal sealed class WebFetchToolFactory(WebFetcher fetcher) : IToolFactory
{
    public ITool? Create(AgentSession session, AgentTurnSelection selection) => new WebFetchTool(fetcher);
}

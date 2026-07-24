using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
    string workingDirectory,
    ProcessRunner processes,
    SystemContextBuilder systemContext,
    Compactor compactor,
    WebFetcher webFetcher) : IAgentSessionFactorySource
{
    public IAgentSessionFactory Create(UserSession owner, ILLMProvider provider) =>
        new AgentSessionFactory(
            owner, workingDirectory, processes, systemContext, compactor, webFetcher);
}

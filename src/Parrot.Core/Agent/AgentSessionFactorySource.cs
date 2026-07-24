using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
    string workingDirectory,
    SessionIndex sessionIndex,
    ProcessRunner processes,
    SystemContextBuilder systemContext,
    Compactor compactor,
    WebFetcher webFetcher) : IAgentSessionFactorySource
{
    public IAgentSessionFactory Create(UserSession owner, ILLMProvider provider) =>
        new AgentSessionFactory(owner, workingDirectory, systemContext, compactor, webFetcher);

    public ShellProcessOwner CreateShellProcesses(UserSession owner) =>
        new(
            workingDirectory,
            sessionIndex.BlobDirectoryFor(owner.Id),
            processes,
            owner.Lifetime);
}

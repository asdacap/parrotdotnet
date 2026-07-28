using Parrot.Context;
using Parrot.Process;
using Parrot.Store;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
    string workingDirectory,
    SessionIndex sessionIndex,
    ProcessRunner processes,
    Compactor compactor,
    WebFetcher webFetcher,
    Llm.ModelRouter router,
    ISystemPromptProvider systemPromptProvider,
    IAgentSessionScopeFactory scopes) : IAgentSessionFactorySource
{
    public IAgentSessionFactory Create(UserSession owner) =>
        new AgentSessionFactory(
            owner,
            workingDirectory,
            compactor,
            webFetcher,
            router,
            systemPromptProvider,
            scopes);

    public ShellProcessOwner CreateShellProcesses(UserSession owner) =>
        new(
            workingDirectory,
            sessionIndex.BlobDirectoryFor(owner.Id),
            processes,
            owner.Lifetime);
}

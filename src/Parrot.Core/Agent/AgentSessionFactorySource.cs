using Parrot.Context;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
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
            owner.Resources.Workspace.LaunchDirectory,
            owner.Resources.BlobDirectory,
            compactor,
            webFetcher,
            router,
            systemPromptProvider,
            scopes);

    public ShellProcessOwners CreateShellProcesses(UserSession owner) =>
        new(owner.Resources, processes, owner.Lifetime);

    public AgentQueueCatalog CreateQueueCatalog(UserSession owner) =>
        new(owner.Resources);
}

using Parrot.Config;
using Parrot.Context;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Tools;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
    ProcessRunner processes,
    Compactor compactor,
    WebFetcher webFetcher,
    ToolDefinitionCatalog toolDefinitions,
    AgentTaskConfig agentTasks,
    IReadOnlyList<string> readOnlyExecCommandPrefixes,
    Llm.ModelRouter router,
    ISystemPromptProvider systemPromptProvider,
    PromptTemplateCatalog promptTemplates) : IAgentSessionFactorySource
{
    public IAgentSessionFactory Create(UserSession owner) =>
        new AgentSessionFactory(
            owner,
            owner.Resources.Workspace.LaunchDirectory,
            compactor,
            webFetcher,
            toolDefinitions,
            agentTasks,
            readOnlyExecCommandPrefixes,
            router,
            new CompositeSystemPromptProvider(
                "runtime:user-session-system-prompt",
                [systemPromptProvider, new AgentHistoryProvider(owner.Resources, promptTemplates)]),
            promptTemplates);

    public ShellProcessOwners CreateShellProcesses(UserSession owner) =>
        new(owner.Resources, processes, owner.Lifetime);

    public AgentQueueCatalog CreateQueueCatalog(UserSession owner) =>
        new(owner.Resources);
}

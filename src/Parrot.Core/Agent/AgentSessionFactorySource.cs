using Parrot.Config;
using Parrot.Context;
using Parrot.Process;
using Parrot.Tools;
using Parrot.Web;

namespace Parrot.Agent;

internal sealed class AgentSessionFactorySource(
    ProcessRunner processes,
    Compactor compactor,
    WebFetcher webFetcher,
    ToolDefinitionCatalog toolDefinitions,
    AgentTaskConfig agentTasks,
    RequestLimitsConfig requestLimits,
    IReadOnlyList<string> readOnlyExecCommandPrefixes,
    Llm.IModelRouter router,
    ISystemPromptProvider systemPromptProvider,
    IPromptTemplateCatalog promptTemplates,
    Func<AgentSessionScopeArguments, IAgentSessionScope, IAgentSessionComposition> composeSession) : IAgentSessionFactorySource
{
    public IAgentSessionFactory Create(IUserSession owner) =>
        new AgentSessionFactory(
            owner,
            processes,
            owner.Resources.Workspace.LaunchDirectory,
            compactor,
            webFetcher,
            toolDefinitions,
            agentTasks,
            requestLimits,
            readOnlyExecCommandPrefixes,
            router,
            new CompositeSystemPromptProvider(
                "runtime:user-session-system-prompt",
                [systemPromptProvider, new AgentHistoryProvider(owner.Resources, promptTemplates)]),
            promptTemplates,
            composeSession);
}

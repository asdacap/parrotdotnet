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
    AgentSendConfig agentSend,
    RequestLimitsConfig requestLimits,
    IReadOnlyList<string> readOnlyExecCommandPrefixes,
    Llm.IModelRouter router,
    IReadOnlyList<ISystemPromptProvider> systemPromptProviders,
    IPromptTemplateCatalog promptTemplates,
    Func<AgentSessionScopeArguments, IAgentSessionScope, AgentSessionComposition> composeSession) : IAgentSessionFactorySource
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
            agentSend,
            requestLimits,
            readOnlyExecCommandPrefixes,
            router,
            [.. systemPromptProviders, new AgentHistoryProvider(owner.Resources, promptTemplates)],
            promptTemplates,
            composeSession);
}

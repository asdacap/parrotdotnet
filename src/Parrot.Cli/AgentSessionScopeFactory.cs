using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Cli;

internal sealed class AgentSessionScopeFactory : IAgentSessionScopeFactory
{
    public IAgentSessionLease Create(
        AgentIdentity identity,
        ModelSelector model,
        ModelRouter router,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IReadOnlyList<IToolFactory> toolFactories,
        ToolDefinitionCatalog toolDefinitions,
        IReadOnlyList<string> readOnlyExecCommandPrefixes,
        ShellProcessOwners shellProcesses,
        ISystemPromptProvider systemPromptProvider,
        AgentScratchDirectory scratch,
        Compactor compactor,
        PromptTemplateCatalog promptTemplates,
        IMode mode,
        AgentSessionSecurity security,
        RuntimeStatus status,
        AgentRegistry registry,
        AgentQueues queues,
        UserSession owner,
        TimeProvider timeProvider,
        CancellationToken lifetime)
    {
        var arguments = new AgentSessionScopeArguments(
            identity,
            model,
            router,
            eventBroker,
            eventRepository,
            toolFactories,
            toolDefinitions,
            readOnlyExecCommandPrefixes,
            shellProcesses,
            systemPromptProvider,
            scratch,
            compactor,
            promptTemplates,
            mode,
            security,
            status,
            registry,
            queues,
            owner,
            timeProvider,
            lifetime);
        var scope = new AgentSessionComposition(arguments);
        return new AgentSessionScope(scope.Session, queues);
    }
}

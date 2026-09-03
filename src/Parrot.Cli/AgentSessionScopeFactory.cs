using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Cli;

internal sealed class AgentSessionScopeFactory : IAgentSessionScopeFactory
{
    public IAgentSessionScope Create(
        AgentIdentity identity,
        AgentSessionParentScope parentScope,
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
        QuestionBroker userQuestions,
        TimeProvider timeProvider,
        CancellationToken lifetime)
    {
        parentScope.Validate(identity);
        var arguments = new AgentSessionScopeArguments(
            identity,
            parentScope,
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
            userQuestions,
            timeProvider,
            lifetime);
        var composition = new AgentSessionComposition(arguments);
        return new AgentSessionScope(
            composition.Session,
            composition.Children,
            composition.ChildQuestions,
            queues);
    }
}

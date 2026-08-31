using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

internal interface IAgentSessionScopeFactory
{
    IAgentSessionLease Create(
        AgentIdentity identity,
        ModelSelector model,
        ModelRouter router,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IReadOnlyList<IToolFactory> toolFactories,
        ToolDocumentationCatalog toolDocumentation,
        ShellProcessOwners shellProcesses,
        ISystemPromptProvider systemPromptProvider,
        AgentScratchDirectory scratch,
        Compactor compactor,
        IMode mode,
        AgentSessionSecurity security,
        RuntimeStatus status,
        AgentRegistry registry,
        AgentQueues queues,
        UserSession owner,
        CancellationToken lifetime);
}

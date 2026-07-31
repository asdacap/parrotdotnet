using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
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
        ShellProcessOwners shellProcesses,
        ISystemPromptProvider systemPromptProvider,
        string blobDirectory,
        Compactor compactor,
        IAgentProfile? profile,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        AgentRegistry registry,
        AgentQueues queues,
        UserSession owner,
        CancellationToken lifetime)
    {
        var arguments = new AgentSessionScopeArguments(
            identity,
            model,
            router,
            eventBroker,
            eventRepository,
            toolFactories,
            shellProcesses,
            systemPromptProvider,
            blobDirectory,
            compactor,
            profile,
            securityProfile,
            status,
            registry,
            queues,
            owner,
            lifetime);
        var scope = new AgentSessionComposition(arguments);
        return new AgentSessionScope(scope.Session, queues);
    }
}

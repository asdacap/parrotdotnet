using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
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
        string workingDirectory,
        string configDirectory,
        string date,
        ModelPromptContext modelPromptContext,
        Compactor compactor,
        MainAgentProfile? profile,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        CancellationToken lifetime)
    {
        var arguments = new AgentSessionScopeArguments(
            identity,
            model,
            router,
            eventBroker,
            eventRepository,
            toolFactories,
            workingDirectory,
            configDirectory,
            date,
            modelPromptContext,
            compactor,
            profile,
            securityProfile,
            status,
            lifetime);
        var scope = new AgentSessionComposition(arguments);
        return new AgentSessionScope(scope.Session);
    }
}

using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Security;
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
        string workingDirectory,
        string configDirectory,
        string date,
        ModelPromptContext modelPromptContext,
        Compactor compactor,
        MainAgentProfile? profile,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        CancellationToken lifetime);
}

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
        ProviderModel model,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IReadOnlyList<IToolFactory> toolFactories,
        string workingDirectory,
        string configDirectory,
        string date,
        Compactor compactor,
        ModeProfile? mode,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        CancellationToken lifetime);
}

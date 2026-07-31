using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
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
        ShellProcessOwners shellProcesses,
        ISystemPromptProvider systemPromptProvider,
        string blobDirectory,
        Compactor compactor,
        IAgentProfile? profile,
        SecurityProfile securityProfile,
        RuntimeStatus? status,
        AgentRegistry registry,
        UserSession owner,
        CancellationToken lifetime);
}

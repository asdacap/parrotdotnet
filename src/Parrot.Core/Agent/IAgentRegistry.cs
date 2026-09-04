using Parrot.Llm;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

internal interface IAgentRegistry : IAsyncDisposable, IActiveWorkSource, IAgentStatusSource
{
    CancellationToken ChildLifetime { get; }

    bool IsAccepting { get; }

    void AttachStatus(RuntimeStatus status);

    void RegisterRootScope(IAgentSessionScope scope);

    void UnregisterRootScope(IAgentSessionScope scope);

    AgentProfile ResolveChildProfile(string profileId);

    RetainedAgentReservation ReserveRetainedAgent();

    RuntimeStatus RequireStatus();

    bool ContainsScope(IAgentSessionScope candidate);

    IAgentSessionScope? FindScope(string sessionId);

    IAgentSessionScope CreateChildScope(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        ModelSelector model,
        IMode mode,
        SecurityProfile securityProfile,
        RuntimeStatus status,
        CancellationToken childLifetime);

    void InitializeChildHistory(
        string parentSessionId,
        string childSessionId,
        long assistantSequence,
        string spawnToolCallId,
        HistoryForkSelection fork);

    void CleanupChildHistory(string childSessionId);
}

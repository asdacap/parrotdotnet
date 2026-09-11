using Parrot.Llm;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Agent;

/// <summary>Coordinates one user session's agent scopes, child admission and shutdown.</summary>
internal interface IAgentRegistry : IAsyncDisposable
{
    CancellationToken ChildLifetime { get; }

    bool IsAccepting { get; }

    /// <summary>Captures current scopes by traversing registered roots and their child topology.</summary>
    IReadOnlyList<IAgentSessionScope> SnapshotScopes();

    /// <summary>Attaches the shared runtime observer once.</summary>
    void AttachStatus(RuntimeStatus status);

    /// <summary>Registers a root scope with a unique session identity.</summary>
    void RegisterRootScope(IAgentSessionScope scope);

    /// <summary>Removes the exact registered root scope.</summary>
    void UnregisterRootScope(IAgentSessionScope scope);

    /// <summary>Reports active descendants ordered by session identity.</summary>
    IReadOnlyList<ActiveWorkObservation> Active();

    /// <summary>Captures active descendant identities for runtime status reporting.</summary>
    IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot();

    /// <summary>Stops admission and begins the single shared asynchronous scope shutdown.</summary>
    ValueTask BeginShutdown();

    IAgentProfile ResolveChildProfile(string profileId);

    /// <summary>Reserves capacity for a retained agent while the registry accepts work.</summary>
    RetainedAgentReservation ReserveRetainedAgent();

    /// <summary>Returns the attached observer, failing if unattached or shutting down.</summary>
    RuntimeStatus RequireStatus();

    /// <summary>Tests scope identity among registered roots and descendants.</summary>
    bool ContainsScope(IAgentSessionScope candidate);

    /// <summary>Finds a root or descendant by session identity.</summary>
    IAgentSessionScope? FindScope(string sessionId);

    /// <summary>Constructs a child scope using this registry's shared session resources.</summary>
    IAgentSessionScope CreateChildScope(
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        ModelSelector model,
        IMode mode,
        SecurityProfile securityProfile,
        RuntimeStatus status,
        EventRepository childHistory,
        CancellationToken childLifetime);

    /// <summary>Initializes child history at the requested parent fork boundary.</summary>
    EventRepository InitializeChildHistory(
        string parentSessionId,
        string childSessionId,
        HistoryForkBoundary boundary,
        HistoryForkSelection fork);
}

namespace Parrot.Agent;

internal interface IChildRegistry
{
    string OwnerSessionId { get; }

    bool IsAccepting { get; }

    IAgentSessionScope SpawnScope(AgentLaunchRequest request);

    IAgentSessionScope? FindDirectChildScope(string childSessionId);

    ValueTask DisposeAsync();

    void ValidateOwner(AgentIdentity identity);

    IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName);

    IAgentSessionScope? FindDescendantScope(string sessionId);

    bool ContainsDescendantScope(IAgentSessionScope candidate);

    IReadOnlyList<IAgentSession> SnapshotDescendants();

    IAgentSessionScope ResolveNamedChildScope(string name);
}

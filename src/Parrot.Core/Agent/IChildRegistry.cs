namespace Parrot.Agent;

internal interface IChildRegistry
{
    string OwnerSessionId { get; }

    bool IsAccepting { get; }

    IAgentSessionScope? FindDirectChildScope(string childSessionId);

    IAgentSessionScope? DetachDirectChildScope(IAgentSessionScope scope);

    void ValidateOwner(AgentIdentity identity);

    IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName);

    IAgentSessionScope? FindDescendantScope(string sessionId);

    bool ContainsDescendantScope(IAgentSessionScope candidate);

    IReadOnlyList<IAgentSession> SnapshotDescendants();

    IAgentSessionScope ResolveNamedChildScope(string name);

    bool ContainsName(string name);

    bool TryAdd(IAgentSessionScope scope);
}

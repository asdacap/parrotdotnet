namespace Parrot.Agent;

internal interface IChildRegistry
{
    string OwnerSessionId { get; }

    bool IsAccepting { get; }

    IAgentSessionScope? FindDirectChildScope(string childSessionId);

    void ValidateOwner(AgentIdentity identity);

    IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName);

    IAgentSessionScope? FindDescendantScope(string sessionId);

    bool ContainsDescendantScope(IAgentSessionScope candidate);

    IReadOnlyList<IAgentSession> SnapshotDescendants();

    IAgentSessionScope ResolveNamedChildScope(string name);

    bool ContainsName(string name);

    void Add(IAgentSessionScope scope);

    IReadOnlyList<IAgentSessionScope> TakeAll();
}

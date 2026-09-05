namespace Parrot.Agent;

internal interface IChildRegistry
{
    bool IsAccepting { get; }

    IAgentSessionScope? FindDirectChildScope(string childSessionId);

    IAgentSessionScope? DetachDirectChildScope(IAgentSessionScope scope);

    IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName);

    IAgentSessionScope? FindDescendantScope(string sessionId);

    bool ContainsDescendantScope(IAgentSessionScope candidate);

    IReadOnlyList<IAgentSession> SnapshotDescendants();

    IAgentSessionScope ResolveNamedChildScope(string name);

    bool TryAdd(IAgentSessionScope scope);
}

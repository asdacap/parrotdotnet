namespace Parrot.Agent;

internal interface IChildRegistry
{
    string OwnerSessionId { get; }

    bool IsAccepting { get; }

    IAgentSessionScope SpawnScope(AgentLaunchRequest request);

    IAgentSessionScope AuthorizeDirectChild(string childSessionId);

    IAgentSession AuthorizeQuestionChild(IAgentSession child);

    ValueTask DisposeAsync();

    void ValidateOwner(AgentIdentity identity);

    void ValidateOwnerScope(IAgentSessionScope scope);

    IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName);

    IAgentSessionScope? FindDescendantScope(string sessionId);

    bool ContainsDescendantScope(IAgentSessionScope candidate);

    IReadOnlyList<IAgentSession> SnapshotDescendants();

    IAgentSessionScope ResolveNamedChildScope(string name);
}

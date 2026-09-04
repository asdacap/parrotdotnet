namespace Parrot.Agent;

internal interface IChildRegistry
{
    string OwnerSessionId { get; }

    IAgentSessionScope SpawnScope(AgentLaunchRequest request);

    IAgentSessionScope AuthorizeDirectChild(string childSessionId);

    IAgentSession AuthorizeQuestionChild(IAgentSession child);

    ValueTask DisposeAsync();

    void ValidateOwner(AgentIdentity identity);

    void AttachOwnerScope(IAgentSessionScope scope);

    void DetachOwnerScope(IAgentSessionScope scope);

    IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName);

    IAgentSessionScope? FindDescendantScope(string sessionId);

    bool ContainsDescendantScope(IAgentSessionScope candidate);

    IReadOnlyList<IAgentSession> SnapshotDescendants();

    Task ReceiveCompletion(AgentIdentity child, AgentExecution completed);

    IAgentSessionScope ResolveNamedChildScope(string name);
}

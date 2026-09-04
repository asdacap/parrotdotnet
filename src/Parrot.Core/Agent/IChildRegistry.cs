using Parrot.Statuses;

namespace Parrot.Agent;

internal interface IChildRegistry
{
    string OwnerSessionId { get; }

    IAgentSession Spawn(AgentLaunchRequest request);

    IAgentSessionScope SpawnScope(AgentLaunchRequest request);

    IAgentSessionScope AuthorizeDirectChild(string childSessionId);

    IAgentSession AuthorizeQuestionChild(IAgentSession child);

    IReadOnlyList<ActiveWorkObservation> ObserveActive();

    ValueTask DisposeAsync();

    void ValidateOwner(AgentIdentity identity);

    void AttachOwnerScope(IAgentSessionScope scope);

    void DetachOwnerScope(IAgentSessionScope scope);

    IAgentSessionScope RequireOwnerScope();

    IAgentSessionScope ResolveDirectChildScope(string sessionIdOrName);

    IAgentSessionScope? FindDescendantScope(string sessionId);

    bool ContainsDescendantScope(IAgentSessionScope candidate);

    IReadOnlyList<IAgentSession> SnapshotDescendants();

    Task ReceiveCompletion(AgentIdentity child, AgentExecution completed);

    IAgentSessionScope ResolveNamedChildScope(string name);
}

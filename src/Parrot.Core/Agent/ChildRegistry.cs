using Parrot.Statuses;

namespace Parrot.Agent;

internal sealed class ChildRegistry(AgentIdentity owner, AgentRegistry authority)
{
    internal string OwnerSessionId => owner.SessionId;

    public AgentSession Spawn(AgentLaunchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Parent);

        if (!ReferenceEquals(request.Parent.Identity, owner))
        {
            throw new AgentRegistryException(
                $"launch parent does not match child registry owner: expected {owner.SessionId}, actual {request.Parent.SessionId}");
        }

        return authority.SpawnFor(owner, this, request);
    }

    public AgentSession ResolveStatusTarget(string sessionIdOrName) =>
        authority.ResolveStatusTargetFor(owner, this, sessionIdOrName);

    public AgentSession AuthorizeDirectChild(string childSessionId) =>
        authority.AuthorizeDirectChildFor(owner, this, childSessionId);

    public AgentSession AuthorizeQuestionChild(AgentSession child) =>
        authority.AuthorizeQuestionChildFor(owner, this, child);

    public AgentSession ResolveRecipient(string sessionIdOrName) =>
        authority.ResolveRecipientFor(owner, this, sessionIdOrName);

    public IReadOnlyList<ActiveWorkObservation> ObserveActive() =>
        authority.ObserveActiveChildrenFor(owner, this);

    internal void ValidateOwner(AgentIdentity identity)
    {
        if (!ReferenceEquals(identity, owner))
        {
            throw new AgentRegistryException(
                $"child registry owner does not match agent identity: expected {owner.SessionId}, actual {identity.SessionId}");
        }
    }
}

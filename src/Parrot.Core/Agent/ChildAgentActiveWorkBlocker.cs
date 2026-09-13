using Parrot.Statuses;

namespace Parrot.Agent;

internal sealed class ChildAgentActiveWorkBlocker(IChildRegistry children, AgentIdentity owner) : IActiveWorkBlocker
{
    public ActiveWorkBlockerResult? Observe()
    {
        var activeChildren = children.SnapshotDescendants()
            .Where(session => session.IsActive()
                && string.Equals(session.ParentSessionId, owner.SessionId, StringComparison.Ordinal))
            .Select(static session => new ActiveWorkObservation(
                session.SessionId,
                session.Name,
                ActiveWorkKind.Agent,
                ActiveWorkState.Running))
            .OrderBy(static observation => observation.Id, StringComparer.Ordinal)
            .ToArray();
        if (activeChildren.Length == 0)
        {
            return null;
        }

        return new ActiveWorkBlockerResult(
            new ActiveWorkSection("Running direct subagents", activeChildren).Format(),
            null);
    }
}

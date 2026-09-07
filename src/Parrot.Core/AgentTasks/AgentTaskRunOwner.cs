using Parrot.Statuses;

namespace Parrot.AgentTasks;

internal sealed class AgentTaskRunOwner(
    string ownerAgentSessionId,
    AgentTaskRunCatalog catalog)
{
    internal string OwnerAgentSessionId { get; } = ownerAgentSessionId;

    public IReadOnlyList<ActiveWorkObservation> Active() =>
        catalog.Active(OwnerAgentSessionId);

    internal void Start(AgentTaskRunRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
            request.OwnerScope.Session.SessionId,
            OwnerAgentSessionId,
            StringComparison.Ordinal))
        {
            throw new InvalidOperationException("AgentTask run owner does not match the requesting agent session.");
        }

        catalog.Start(request, cancellationToken);
    }

    internal IReadOnlyList<AgentTaskRunSnapshot> Snapshot() =>
        catalog.Snapshot(OwnerAgentSessionId);
}

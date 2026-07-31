using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Statuses;

internal sealed class RuntimeStatus
{
    private readonly StatusRegistry _activity;
    private readonly StatusRegistry _full;

    public RuntimeStatus(
        AgentQueueCatalog queues,
        IActiveWorkSource processes,
        IActiveWorkSource subagents)
    {
        var queueStatus = new QueueStatusProvider(queues);
        var processStatus = new ActiveWorkStatusProvider(ActiveWorkKind.Shell, processes);
        var subagentStatus = new ActiveWorkStatusProvider(ActiveWorkKind.Agent, subagents);
        _activity = new StatusRegistry(queueStatus, processStatus, subagentStatus);
        _full = new StatusRegistry(new SelectionStatusProvider(), queueStatus, processStatus, subagentStatus);
    }

    public Task<string> Observe(
        AgentSession session,
        AgentTurnSelection selection,
        IAgentProfile profile,
        CancellationToken cancellationToken) =>
        _full.Observe(
            Query(session, selection, profile.Id),
            new ProfileStatusProvider($"profile:{profile.Id}", profile.Prompt),
            cancellationToken);

    public Task<string> ObserveActivity(
        AgentSession session,
        AgentTurnSelection selection,
        CancellationToken cancellationToken) =>
        _activity.Observe(Query(session, selection, selection.Profile.Id), null, cancellationToken);

    private static StatusQuery Query(
        AgentSession session,
        AgentTurnSelection selection,
        string profile) =>
        new(
            session.SessionId,
            session.ParentSessionId,
            session.ParentSessionName,
            profile,
            selection.RequestedModel.Value);
}

using Parrot.Agent;
using Parrot.Config;
using Parrot.Process;
using Parrot.Queues;

namespace Parrot.Statuses;

internal sealed class RuntimeStatus
{
    private readonly StatusRegistry _activity;
    private readonly StatusRegistry _full;

    public RuntimeStatus(
        AgentQueueCatalog queues,
        IProcessStatusSource processes,
        IAgentStatusSource subagents,
        PromptTemplateCatalog templates,
        TimeProvider timeProvider)
    {
        var generatedTime = new GeneratedTimeStatusProvider(timeProvider, templates);
        var runtime = new RuntimeTreeStatusProvider(queues, processes, subagents, templates);
        _activity = new StatusRegistry(runtime);
        _full = new StatusRegistry(generatedTime, new SelectionStatusProvider(templates), runtime);
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

    public Task<string> ObserveRuntime(
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

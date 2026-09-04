using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;

namespace Parrot.Statuses;

internal sealed class RuntimeStatus
{
    private readonly PromptTemplateCatalog _templates;
    private readonly StatusRegistry _activity;
    private readonly StatusRegistry _full;

    public RuntimeStatus(
        AgentQueueCatalog queues,
        IProcessStatusSource processes,
        IAgentStatusSource subagents,
        PromptTemplateCatalog templates,
        TimeProvider timeProvider)
    {
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        var generatedTime = new GeneratedTimeStatusProvider(timeProvider, templates);
        var runtime = new RuntimeTreeStatusProvider(queues, processes, subagents, templates);
        _activity = new StatusRegistry(runtime);
        _full = new StatusRegistry(generatedTime, new SelectionStatusProvider(templates), runtime);
    }

    public Task<string> ObserveWithTools(
        IAgentSession session,
        AgentTurnSelection selection,
        IAgentProfile profile,
        IReadOnlyList<LLMToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tools);
        var context = new ContextStatusProvider(session.EstimateContextForTools(selection, tools), _templates);
        return _full.ObserveWithProvider(
            Query(session, selection, profile.Id),
            new ProfileStatusProvider($"profile:{profile.Id}", profile.Prompt),
            context,
            cancellationToken);
    }

    public Task<string> Observe(
        IAgentSession session,
        AgentTurnSelection selection,
        IAgentProfile profile,
        CancellationToken cancellationToken) =>
        ObserveWithTools(session, selection, profile, session.AdvertisedToolDefinitions(selection), cancellationToken);

    public Task<string> ObserveRuntime(
        IAgentSession session,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        var context = new ContextStatusProvider(session.EstimateContext(selection), _templates);
        return _activity.ObserveWithProvider(
            Query(session, selection, selection.Profile.Id),
            null,
            context,
            cancellationToken);
    }

    internal Task<string> ObserveWithContext(
        IAgentSession session,
        AgentTurnSelection selection,
        IAgentProfile profile,
        ContextSnapshot contextSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(contextSnapshot);
        return _full.ObserveWithProvider(
            Query(session, selection, profile.Id),
            new ProfileStatusProvider($"profile:{profile.Id}", profile.Prompt),
            new ContextStatusProvider(contextSnapshot, _templates),
            cancellationToken);
    }

    internal async Task<string> ObserveContext(
        IAgentSession session,
        AgentTurnSelection selection,
        ContextSnapshot contextSnapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(contextSnapshot);
        var observation = await new ContextStatusProvider(contextSnapshot, _templates)
            .Observe(Query(session, selection, selection.Profile.Id), cancellationToken)
            .ConfigureAwait(false);
        return observation.Available ? observation.Text : string.Empty;
    }

    private static StatusQuery Query(
        IAgentSession session,
        AgentTurnSelection selection,
        string profile) =>
        new(
            session.SessionId,
            session.ParentSessionId,
            session.ParentSessionName,
            profile,
            selection.RequestedModel.Value);
}

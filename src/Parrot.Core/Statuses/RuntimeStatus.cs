using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Context;
using Parrot.Llm;

namespace Parrot.Statuses;

internal sealed class RuntimeStatus
{
    private readonly PromptTemplateCatalog _templates;
    private readonly StatusRegistry _activity;
    private readonly StatusRegistry _full;

    public RuntimeStatus(
        IAgentRegistry agents,
        PromptTemplateCatalog templates,
        TimeProvider timeProvider)
        : this(agents, templates, timeProvider, null)
    {
    }

    public RuntimeStatus(
        IAgentRegistry agents,
        PromptTemplateCatalog templates,
        TimeProvider timeProvider,
        AgentTaskRunCatalog? agentTaskRuns)
    {
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        var generatedTime = new GeneratedTimeStatusProvider(timeProvider, templates);
        var runtime = new RuntimeTreeStatusProvider(agents, templates);
        var agentTasks = agentTaskRuns is null ? null : new AgentTaskStatusProvider(agentTaskRuns, templates);
        _activity = agentTasks is null
            ? new StatusRegistry(runtime)
            : new StatusRegistry(runtime, agentTasks);
        _full = agentTasks is null
            ? new StatusRegistry(generatedTime, new SelectionStatusProvider(templates), runtime)
            : new StatusRegistry(generatedTime, new SelectionStatusProvider(templates), runtime, agentTasks);
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
        IStatusProvider contextStatus = new ContextStatusProvider(session.EstimateContextForTools(selection, tools), _templates);
        return _full.ObserveWithProvider(
            new StatusQuery(
                session.SessionId,
                session.ParentSessionId,
                session.ParentSessionName,
                profile.Id,
                selection.RequestedModel.Value),
            new ProfileStatusProvider($"profile:{profile.Id}", profile.Prompt),
            contextStatus,
            cancellationToken);
    }

    public Task<string> ObserveRuntime(
        IAgentSession session,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        IStatusProvider contextStatus = new ContextStatusProvider(session.EstimateContext(selection), _templates);
        return _activity.ObserveWithProvider(
            new StatusQuery(
                session.SessionId,
                session.ParentSessionId,
                session.ParentSessionName,
                selection.Profile.Id,
                selection.RequestedModel.Value),
            null,
            contextStatus,
            cancellationToken);
    }

    public async Task<string> ObserveStatistics(
        IAgentSession session,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        var runtime = await ObserveRuntime(session, selection, cancellationToken).ConfigureAwait(false);
        var statistics = new StatusRegistry(new StatisticsStatusProvider(session.CaptureStatistics(), _templates));
        var observation = await statistics.Observe(
            new StatusQuery(
                session.SessionId,
                session.ParentSessionId,
                session.ParentSessionName,
                selection.Profile.Id,
                selection.RequestedModel.Value),
            null,
            cancellationToken).ConfigureAwait(false);
        return string.Join("\n\n", new[] { runtime, observation }.Where(text => !string.IsNullOrWhiteSpace(text)));
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
            new StatusQuery(
                session.SessionId,
                session.ParentSessionId,
                session.ParentSessionName,
                profile.Id,
                selection.RequestedModel.Value),
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
        var contextStatus = ContextStatusProvider.Create(contextSnapshot, _templates);
        var observation = await contextStatus.Observe(
            new StatusQuery(
                session.SessionId,
                session.ParentSessionId,
                session.ParentSessionName,
                selection.Profile.Id,
                selection.RequestedModel.Value),
            cancellationToken)
            .ConfigureAwait(false);
        return observation.Available ? observation.Text : string.Empty;
    }
}

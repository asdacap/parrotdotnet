using Parrot.Agent;
using Parrot.AgentTasks;
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
        : this(queues, processes, subagents, templates, timeProvider, null)
    {
    }

    public RuntimeStatus(
        AgentQueueCatalog queues,
        IProcessStatusSource processes,
        IAgentStatusSource subagents,
        PromptTemplateCatalog templates,
        TimeProvider timeProvider,
        AgentTaskRunCatalog? agentTaskRuns)
    {
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        var generatedTime = new GeneratedTimeStatusProvider(timeProvider, templates);
        var runtime = new RuntimeTreeStatusProvider(queues, processes, subagents, templates);
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
        IAgentSessionContext context,
        AgentTurnSelection selection,
        IAgentProfile profile,
        IReadOnlyList<LLMToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(tools);
        var contextStatus = new ContextStatusProvider(context.EstimateContextForTools(selection, tools), _templates);
        return _full.ObserveWithProvider(
            Query(session, selection, profile.Id),
            new ProfileStatusProvider($"profile:{profile.Id}", profile.Prompt),
            contextStatus,
            cancellationToken);
    }

    public Task<string> ObserveRuntime(
        IAgentSession session,
        IAgentSessionContext context,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(selection);
        var contextStatus = new ContextStatusProvider(context.EstimateContext(selection), _templates);
        return _activity.ObserveWithProvider(
            Query(session, selection, selection.Profile.Id),
            null,
            contextStatus,
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

using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;

namespace Parrot.Statuses;

internal sealed class RuntimeStatus : IRuntimeStatus
{
    private readonly IPromptTemplateCatalog _templates;
    private readonly StatusRegistry _activity;
    private readonly StatusRegistry _full;

    public RuntimeStatus(
        IPromptTemplateCatalog templates,
        TimeProvider timeProvider,
        IReadOnlyList<IStatusProvider> runtimeProviders)
    {
        _templates = templates ?? throw new ArgumentNullException(nameof(templates));
        ArgumentNullException.ThrowIfNull(runtimeProviders);
        _activity = new StatusRegistry([.. runtimeProviders]);
        _full = new StatusRegistry([new GeneratedTimeStatusProvider(timeProvider, templates), new SelectionStatusProvider(templates), .. runtimeProviders]);
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
            StatusQuery.Create(session, selection.Profile.Id, selection.RequestedModel.Value),
            null,
            contextStatus,
            cancellationToken);
    }

    public Task<string> ObserveStatistics(
        IAgentSession session,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(selection);
        var statistics = new StatusRegistry(new StatisticsStatusProvider(session.CaptureStatistics(), _templates));
        return statistics.Observe(
            StatusQuery.Create(session, selection.Profile.Id, selection.RequestedModel.Value),
            null,
            cancellationToken);
    }

    public Task<string> ObserveWithContext(
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
            StatusQuery.Create(session, profile.Id, selection.RequestedModel.Value),
            new ProfileStatusProvider($"profile:{profile.Id}", profile.Prompt),
            new ContextStatusProvider(contextSnapshot, _templates),
            cancellationToken);
    }

    public async Task<string> ObserveFitted(
        IAgentSession session,
        AgentTurnSelection selection,
        IAgentProfile profile,
        Func<string, ContextSnapshot> estimate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        const int maximumConvergenceAttempts = 8;
        var context = estimate(string.Empty);
        var content = await ObserveWithContext(session, selection, profile, context, cancellationToken).ConfigureAwait(false);
        for (var attempt = 0; attempt < maximumConvergenceAttempts; attempt++)
        {
            var rendered = estimate(content);
            if (rendered == context)
            {
                break;
            }

            context = rendered;
            content = await ObserveWithContext(session, selection, profile, context, cancellationToken).ConfigureAwait(false);
        }

        return content;
    }
}

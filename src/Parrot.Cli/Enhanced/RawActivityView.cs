using System.Text;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RawActivityView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    Func<CancellationToken, Task> delay,
    ToolPresenterRegistry presenters,
    Func<string, CancellationToken, Task> updateMainAgentActivity,
    Func<bool> invalidate) : IDisposable
{
    private const int SpinnerIntervalMilliseconds = 80;

    private readonly List<(AgentSessionState State, string ActivityId)> _activities = [];
    private readonly Dictionary<string, AgentSessionState> _agentSessions = new(StringComparer.Ordinal);
    private readonly AgentSessionHierarchy _hierarchy = new();

    private readonly StringBuilder _reasoning = new();
    private readonly SemaphoreSlim _rendering = new(1, 1);

    private IReadOnlyList<ILiveBufferItem> _content = [];
    private int _frame;

    public RawActivityView(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        ToolPresenterRegistry presenters,
        Func<string, CancellationToken, Task> updateMainAgentActivity)
        : this(replace, commit, presenters, updateMainAgentActivity, static () => false)
    {
    }

    public RawActivityView(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        ToolPresenterRegistry presenters,
        Func<string, CancellationToken, Task> updateMainAgentActivity,
        Func<bool> invalidate)
        : this(
            replace,
            commit,
            static cancellationToken => Task.Delay(SpinnerIntervalMilliseconds, cancellationToken),
            presenters,
            updateMainAgentActivity,
            invalidate)
    {
    }

    internal RawActivityView(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        Func<CancellationToken, Task> delay,
        ToolPresenterRegistry presenters,
        Func<string, CancellationToken, Task> updateMainAgentActivity)
        : this(replace, commit, delay, presenters, updateMainAgentActivity, static () => false)
    {
    }

    public void Dispose() => _rendering.Dispose();

    public async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await delay(cancellationToken).ConfigureAwait(false);
                await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    _frame++;
                    if (_activities.Count > 0)
                    {
                        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                        _ = invalidate();
                    }
                }
                finally
                {
                    _ = _rendering.Release();
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task ReplaceContent(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _content = Capture(items);
            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    public async Task CommitContent(
        IScrollbackItem scrollback,
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);
        ArgumentNullException.ThrowIfNull(items);

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _content = Capture(items);
            await commit(scrollback, Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    public async Task Prepare(Event published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (_reasoning.Length == 0 || published.PayloadCase == Event.PayloadOneofCase.ReasoningChunk)
        {
            return;
        }

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = _reasoning.Clear();
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    public async Task Render(Event published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (published.PayloadCase == Event.PayloadOneofCase.QueueSnapshot)
        {
            return;
        }

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _hierarchy.Observe(published);
            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.AgentStarted:
                    await UpdateAgentName(
                        published.AgentSessionId,
                        published.AgentStarted.Name,
                        cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.AgentStatisticsUpdated:
                    await UpdateAgentStatistics(
                        published.AgentSessionId,
                        published.AgentStatisticsUpdated,
                        cancellationToken).ConfigureAwait(false);
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.AgentFinished:
                    await FinishAgent(published, failed: false, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.AgentFailed:
                    await FinishAgent(published, failed: true, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.TurnStarted:
                    await StartTurn(
                        published.AgentSessionId,
                        LiveModelAliasIcon.Convert(published.TurnStarted.ModelAliasIcon),
                        cancellationToken).ConfigureAwait(false);
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.TurnEnded:
                    await FinishTurn(published, failed: false, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.TurnFailed:
                    await FinishTurn(published, failed: true, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ToolCallChunk:
                    ToolCall(published.AgentSessionId, published.ToolCallChunk);
                    break;
                case Event.PayloadOneofCase.TextChunk when _hierarchy.IsChild(published.AgentSessionId):
                    GetNamedAgentSession(published.AgentSessionId).CollectResponse(published.TextChunk.Fragment);
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ToolStarted:
                    StartTool(published);
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ToolFinished:
                case Event.PayloadOneofCase.ToolCancelled:
                case Event.PayloadOneofCase.ToolError:
                    await FinishTool(published, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ReasoningChunk when _hierarchy.IsRoot(published.AgentSessionId):
                {
                    var fragment = TerminalText.Sanitize(published.ReasoningChunk.Fragment);
                    if (published.ReasoningChunk.Kind == ReasoningKind.Summary)
                    {
                        _ = _reasoning.Clear();
                        if (fragment.Length > 0)
                        {
                            await commit(
                                new ReasoningSummaryScrollbackValue(fragment),
                                Snapshot(),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        _ = _reasoning.Append(fragment);
                        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

                default:
                    break;
            }
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    private List<ILiveBufferItem> Snapshot()
    {
        var items = new List<ILiveBufferItem>(_content.Count + _activities.Count + 1);
        items.AddRange(_content.Select(item => item is MarqueeValue value ? value.Animate(_frame - value.Frame) : item));
        if (_reasoning.Length > 0)
        {
            items.Add(new SpinnerValue("Thinking…", _frame));
        }

        // Keep main-agent activity on the modeline, never in the live buffer.
        var activities = _activities
            .Where(activity =>
                (!_hierarchy.IsRoot(activity.State.AgentSessionId)
                 || !activity.State.IsAgentActivity(activity.ActivityId))
                && !activity.State.IsFoldedActivity(activity.ActivityId))
            .ToList();
        var order = _hierarchy.GetPostOrder(activities.Select(static activity => activity.State.AgentSessionId));
        items.AddRange(activities
            .OrderBy(activity => order[activity.State.AgentSessionId])
            .ThenBy(activity => activity.State.IsAgentActivity(activity.ActivityId) ? 1 : 0)
            .ThenBy(static activity => activity.ActivityId, StringComparer.Ordinal)
            .Select(CreateActivityItem));
        return items;
    }

    private IReadOnlyList<ILiveBufferItem> Capture(IReadOnlyList<ILiveBufferItem> items) =>
        [.. items.Select(item => item is MarqueeValue value ? value.Animate(_frame) : item)];

    private AgentSessionState GetAgentSession(string agentSessionId)
    {
        if (_agentSessions.TryGetValue(agentSessionId, out var state))
        {
            return state;
        }

        state = new AgentSessionState(agentSessionId);
        _agentSessions.Add(agentSessionId, state);
        return state;
    }

    private AgentSessionState GetNamedAgentSession(string agentSessionId)
    {
        var state = GetAgentSession(agentSessionId);
        if (state.HasName)
        {
            return state;
        }

        state.UpdateName(agentSessionId.Length == 0 || _hierarchy.IsRoot(agentSessionId)
            ? "main"
            : _hierarchy.GetLabel(agentSessionId) ?? agentSessionId);
        return state;
    }

    private async Task StartTurn(
        string agentSessionId,
        LiveModelAliasIcon? modelAliasIcon,
        CancellationToken cancellationToken)
    {
        var state = GetNamedAgentSession(agentSessionId);
        if (state.StartTurn(modelAliasIcon) is { } activityId)
        {
            _activities.Add((state, activityId));
            if (_hierarchy.IsRoot(agentSessionId))
            {
                await updateMainAgentActivity(state.ModelineLabel, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task FinishTurn(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (state.FinishTurn(published, failed) is not { } completion)
        {
            return;
        }

        if (_hierarchy.IsRoot(published.AgentSessionId))
        {
            await updateMainAgentActivity(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await CommitCompletion(state, completion, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAgent(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (state.FinishAgent(published, failed) is not { } completion)
        {
            return;
        }

        if (_hierarchy.IsRoot(published.AgentSessionId))
        {
            await updateMainAgentActivity(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await CommitCompletion(state, completion, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAgentName(string agentSessionId, string name, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(agentSessionId);
        state.UpdateName(name);
        await UpdateMainAgentActivity(agentSessionId, state, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAgentStatistics(
        string agentSessionId,
        AgentStatisticsUpdatedEvent statistics,
        CancellationToken cancellationToken)
    {
        var state = GetAgentSession(agentSessionId);
        state.UpdateStatistics(statistics);
        await UpdateMainAgentActivity(agentSessionId, state, cancellationToken).ConfigureAwait(false);
    }

    private Task UpdateMainAgentActivity(
        string agentSessionId,
        AgentSessionState state,
        CancellationToken cancellationToken) =>
        _hierarchy.IsRoot(agentSessionId) && _activities.Any(activity =>
            ReferenceEquals(activity.State, state) && state.IsAgentActivity(activity.ActivityId))
            ? updateMainAgentActivity(state.ModelineLabel, cancellationToken)
            : Task.CompletedTask;

    private void ToolCall(string agentSessionId, ToolCallChunk chunk) =>
        GetAgentSession(agentSessionId).CollectToolCall(chunk);

    private void StartTool(Event published)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        var metadata = presenters.Describe(published.ToolStarted.ToolName);
        var foldIntoAgentStatus = metadata.Modeline
            && !metadata.TerminalOnly
            && !_hierarchy.IsRoot(published.AgentSessionId);
        if (state.StartTool(published.ToolStarted, foldIntoAgentStatus) is { } activityId
            && !metadata.TerminalOnly
            && !(metadata.Modeline && _hierarchy.IsRoot(published.AgentSessionId)))
        {
            _activities.Add((state, activityId));
        }
    }

    private async Task FinishTool(Event published, CancellationToken cancellationToken)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        var (activityId, scrollback) = state.FinishTool(published, presenters);
        _ = _activities.Remove((state, activityId));
        if (scrollback is null)
        {
            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await commit(Wrap(state, scrollback, null), Snapshot(), cancellationToken).ConfigureAwait(false);
        }
    }

    private ILiveBufferItem CreateActivityItem((AgentSessionState State, string ActivityId) activity)
    {
        var value = activity.State.CreateLiveBufferItem(activity.ActivityId, _frame, presenters);
        var modelAliasIcon = _hierarchy.IsChild(activity.State.AgentSessionId)
            && activity.State.IsAgentActivity(activity.ActivityId)
                ? activity.State.ModelAliasIcon
                : null;
        return new HierarchicalLiveValue(
            value,
            _hierarchy.GetDepth(activity.State.AgentSessionId),
            _hierarchy.GetLabel(activity.State.AgentSessionId),
            activity.State.Name,
            "♟",
            modelAliasIcon);
    }

    private HierarchicalScrollbackValue Wrap(
        AgentSessionState state,
        IScrollbackItem value,
        string? successfulIcon) =>
        new(
            value,
            _hierarchy.GetDepth(state.AgentSessionId),
            _hierarchy.GetLabel(state.AgentSessionId),
            state.Name,
            successfulIcon);

    private async Task CommitCompletion(
        AgentSessionState state,
        (string ActivityId, string Response, string Line) completion,
        CancellationToken cancellationToken)
    {
        _ = _activities.Remove((state, completion.ActivityId));
        if (completion.Response.Length > 0)
        {
            await commit(
                Wrap(state, new MarkdownScrollbackValue(completion.Response), null),
                Snapshot(),
                cancellationToken).ConfigureAwait(false);
        }

        await commit(
            Wrap(state, ImmediateScrollbackValue.Muted([completion.Line]), "♟"),
            Snapshot(),
            cancellationToken).ConfigureAwait(false);
    }
}

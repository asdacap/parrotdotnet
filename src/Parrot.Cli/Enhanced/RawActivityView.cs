using System.Text;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RawActivityView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    Func<CancellationToken, Task> delay,
    ToolPresenterRegistry presenters) : IDisposable
{
    private const int SpinnerIntervalMilliseconds = 80;

    private readonly List<(AgentSessionState State, string ActivityId)> _activities = [];
    private readonly Dictionary<string, AgentSessionState> _agentSessions = new(StringComparer.Ordinal);
    private readonly AgentSessionHierarchy _hierarchy = new();
    private readonly Dictionary<string, AgentCompletion> _pendingCompletions = new(StringComparer.Ordinal);

    private readonly StringBuilder _reasoning = new();
    private readonly SemaphoreSlim _rendering = new(1, 1);

    private IReadOnlyList<ILiveBufferItem> _content = [];
    private int _frame;

    public RawActivityView(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        ToolPresenterRegistry presenters)
        : this(
            draw,
            commit,
            static cancellationToken => Task.Delay(SpinnerIntervalMilliseconds, cancellationToken),
            presenters)
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
                        await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
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

    public async Task DrawContent(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _content = [.. items];
            await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
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
            _content = [.. items];
            await commit(scrollback, Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    public async Task Redraw(CancellationToken cancellationToken)
    {
        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
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

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _hierarchy.Observe(published);
            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.AgentStarted:
                    GetAgentSession(published.AgentSessionId).UpdateName(published.AgentStarted.Name);
                    break;
                case Event.PayloadOneofCase.AgentStatisticsUpdated:
                    GetAgentSession(published.AgentSessionId).UpdateStatistics(published.AgentStatisticsUpdated);
                    await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.AgentFinished:
                    await FinishAgent(published, failed: false, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.AgentFailed:
                    await FinishAgent(published, failed: true, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.TurnStarted:
                    StartTurn(published.AgentSessionId);
                    await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
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
                    await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ToolStarted:
                    StartTool(published);
                    await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
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
                                ImmediateScrollbackValue.Muted([$"✦ {fragment}"]),
                                Snapshot(),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        _ = _reasoning.Append(fragment);
                        await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
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
        items.AddRange(_content);
        if (_reasoning.Length > 0)
        {
            items.Add(new SpinnerValue("Thinking…", _frame));
        }

        var order = _hierarchy.GetPostOrder(_activities.Select(static activity => activity.State.AgentSessionId));
        items.AddRange(_activities
            .OrderBy(activity => order[activity.State.AgentSessionId])
            .ThenBy(static activity => string.Equals(activity.ActivityId, "agent", StringComparison.Ordinal) ? 1 : 0)
            .ThenBy(static activity => activity.ActivityId, StringComparer.Ordinal)
            .Select(CreateActivityItem));
        return items;
    }

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

    private void StartTurn(string agentSessionId)
    {
        var state = GetNamedAgentSession(agentSessionId);
        if (state.StartTurn() is { } activityId)
        {
            _activities.Add((state, activityId));
        }
    }

    private async Task FinishTurn(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (state.FinishTurn(published, failed) is not { } completion)
        {
            return;
        }

        _ = _pendingCompletions.TryAdd(state.AgentSessionId, new AgentCompletion(
            completion.ActivityId,
            completion.Response,
            completion.Line,
            failed));
        await FlushCompletions(cancellationToken).ConfigureAwait(false);
        if (_pendingCompletions.ContainsKey(state.AgentSessionId))
        {
            await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinishAgent(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (state.FinishAgent(published, failed) is not { } completion)
        {
            return;
        }

        _ = _pendingCompletions.TryAdd(state.AgentSessionId, new AgentCompletion(
            completion.ActivityId,
            completion.Response,
            completion.Line,
            failed));
        await FlushCompletions(cancellationToken).ConfigureAwait(false);
        if (_pendingCompletions.ContainsKey(state.AgentSessionId))
        {
            await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
    }

    private void ToolCall(string agentSessionId, ToolCallChunk chunk) =>
        GetAgentSession(agentSessionId).CollectToolCall(chunk);

    private void StartTool(Event published)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        var metadata = presenters.Describe(published.ToolStarted.ToolName);
        if (state.StartTool(published.ToolStarted) is { } activityId
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
            await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await commit(Wrap(state, scrollback, null), Snapshot(), cancellationToken).ConfigureAwait(false);
        }

        await FlushCompletions(cancellationToken).ConfigureAwait(false);
    }

    private ILiveBufferItem CreateActivityItem((AgentSessionState State, string ActivityId) activity)
    {
        var value = _pendingCompletions.TryGetValue(activity.State.AgentSessionId, out var completion)
            && string.Equals(completion.ActivityId, activity.ActivityId, StringComparison.Ordinal)
                ? new LiveTextValue(completion.Line)
                : activity.State.CreateLiveBufferItem(activity.ActivityId, _frame, presenters);
        return new HierarchicalLiveValue(
            value,
            _hierarchy.GetDepth(activity.State.AgentSessionId),
            _hierarchy.GetLabel(activity.State.AgentSessionId),
            activity.State.Name,
            "♟");
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

    private async Task FlushCompletions(CancellationToken cancellationToken)
    {
        while (true)
        {
            var ready = _pendingCompletions.Keys
                .Where(sessionId => !_activities.Any(activity =>
                    string.Equals(activity.State.AgentSessionId, sessionId, StringComparison.Ordinal)
                        ? !string.Equals(
                            activity.ActivityId,
                            _pendingCompletions[sessionId].ActivityId,
                            StringComparison.Ordinal)
                        : !_pendingCompletions[sessionId].Failed
                            && _hierarchy.IsDescendant(activity.State.AgentSessionId, sessionId)))
                .OrderByDescending(_hierarchy.GetDepth)
                .ThenBy(static sessionId => sessionId, StringComparer.Ordinal)
                .FirstOrDefault();
            if (ready is null)
            {
                if (_pendingCompletions.Count > 0)
                {
                    await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            var completion = _pendingCompletions[ready];
            _ = _pendingCompletions.Remove(ready);
            var (activityId, response, line, _) = completion;
            var state = _agentSessions[ready];
            _ = _activities.Remove((state, activityId));
            if (response.Length > 0)
            {
                var responseLines = response.Split('\n');
                responseLines[0] = $"● {responseLines[0]}";
                await commit(
                    Wrap(state, ImmediateScrollbackValue.Muted(responseLines), null),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
            }

            await commit(
                Wrap(state, ImmediateScrollbackValue.Muted([line]), "♟"),
                Snapshot(),
                cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record AgentCompletion(string ActivityId, string Response, string Line, bool Failed);
}

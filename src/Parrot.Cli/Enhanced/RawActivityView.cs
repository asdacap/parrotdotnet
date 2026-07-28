using System.Text;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RawActivityView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    Func<CancellationToken, Task> delay) : IDisposable
{
    private const int SpinnerIntervalMilliseconds = 80;

    private readonly List<(AgentSessionState State, string ActivityId)> _activities = [];
    private readonly Dictionary<string, AgentSessionState> _agentSessions = new(StringComparer.Ordinal);
    private readonly StringBuilder _reasoning = new();
    private readonly SemaphoreSlim _rendering = new(1, 1);

    private IReadOnlyList<ILiveBufferItem> _content = [];
    private int _frame;
    private string? _mainSessionId;

    public RawActivityView(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit)
        : this(
            draw,
            commit,
            static cancellationToken => Task.Delay(SpinnerIntervalMilliseconds, cancellationToken))
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
            if (_reasoning.Length > 0)
            {
                var reasoning = _reasoning.ToString();
                _ = _reasoning.Clear();
                await commit(
                    ImmediateScrollbackValue.Muted([reasoning]),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
            }
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
                case Event.PayloadOneofCase.ToolStarted:
                    StartTool(published);
                    await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ToolFinished:
                case Event.PayloadOneofCase.ToolCancelled:
                case Event.PayloadOneofCase.ToolError:
                    await FinishTool(published, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ReasoningChunk:
                    _ = _reasoning.Append(TerminalText.Sanitize(published.ReasoningChunk.Fragment));
                    await draw(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
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
            items.Add(new LiveTextValue(_reasoning.ToString()));
        }

        items.AddRange(_activities.Select(activity =>
            activity.State.CreateLiveBufferItem(activity.ActivityId, _frame)));
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

        _mainSessionId ??= agentSessionId;
        state.UpdateName(string.Equals(_mainSessionId, agentSessionId, StringComparison.Ordinal)
            ? "main"
            : agentSessionId);
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

        _ = _activities.Remove((state, completion.ActivityId));
        await commit(
            ImmediateScrollbackValue.Muted([completion.Line]),
            Snapshot(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAgent(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (state.FinishAgent(published, failed) is not { } completion)
        {
            return;
        }

        _ = _activities.Remove((state, completion.ActivityId));
        await commit(
            ImmediateScrollbackValue.Muted([completion.Line]),
            Snapshot(),
            cancellationToken).ConfigureAwait(false);
    }

    private void ToolCall(string agentSessionId, ToolCallChunk chunk) =>
        GetAgentSession(agentSessionId).CollectToolCall(chunk);

    private void StartTool(Event published)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        if (state.StartTool(published.ToolStarted) is { } activityId)
        {
            _activities.Add((state, activityId));
        }
    }

    private async Task FinishTool(Event published, CancellationToken cancellationToken)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        var (activityId, line) = state.FinishTool(published);
        _ = _activities.Remove((state, activityId));
        await commit(
            ImmediateScrollbackValue.Muted([line]),
            Snapshot(),
            cancellationToken).ConfigureAwait(false);
    }
}

using System.Globalization;
using System.Text;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RawActivityView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    Func<CancellationToken, Task> delay) : IDisposable
{
    private const int SpinnerIntervalMilliseconds = 80;
    private const string AgentActivity = "agent";
    private const string ToolActivityPrefix = "tool:";

    private readonly List<(string AgentSessionId, string ActivityId)> _activities = [];
    private readonly Dictionary<(string AgentSessionId, string ActivityId), string> _activityLabels = [];
    private readonly HashSet<string> _completedTurns = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AgentStatisticsUpdatedEvent> _statistics = new(StringComparer.Ordinal);
    private readonly StringBuilder _reasoning = new();
    private readonly SemaphoreSlim _rendering = new(1, 1);
    private readonly Dictionary<(string AgentSessionId, string ToolCallId), (string Name, StringBuilder Arguments)>
        _toolCalls = [];

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
                    _names[published.AgentSessionId] = published.AgentStarted.Name;
                    UpdateAgentLabel(published.AgentSessionId);
                    break;
                case Event.PayloadOneofCase.AgentStatisticsUpdated:
                    _statistics[published.AgentSessionId] = published.AgentStatisticsUpdated;
                    UpdateAgentLabel(published.AgentSessionId);
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

    private static string ActivityId(string toolCallId) => ToolActivityPrefix + toolCallId;

    private static string ToolCallDescription((string Name, StringBuilder Arguments) toolCall)
    {
        var name = TerminalText.Sanitize(toolCall.Name);
        var arguments = TerminalText.Sanitize(toolCall.Arguments.ToString());
        return arguments.Length == 0 ? name : $"tool call {name}: {arguments}";
    }

    private static (string ToolCallId, string ToolName) TerminalTool(Event published) => published.PayloadCase switch
    {
        Event.PayloadOneofCase.ToolFinished =>
            (published.ToolFinished.ToolCallId, published.ToolFinished.ToolName),
        Event.PayloadOneofCase.ToolCancelled =>
            (published.ToolCancelled.ToolCallId, published.ToolCancelled.ToolName),
        Event.PayloadOneofCase.ToolError =>
            (published.ToolError.ToolCallId, published.ToolError.ToolName),
        _ => (string.Empty, string.Empty),
    };

    private static string TokenCount(long count) => count switch
    {
        >= 1_000_000 => $"{(count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}m",
        >= 1_000 => $"{(count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture)}k",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    private static string ContextLimit(long limit) => limit == 0 ? "?" : TokenCount(limit);

    private List<ILiveBufferItem> Snapshot()
    {
        var items = new List<ILiveBufferItem>(_content.Count + _activities.Count + 1);
        items.AddRange(_content);
        if (_reasoning.Length > 0)
        {
            items.Add(new LiveTextValue(_reasoning.ToString()));
        }

        items.AddRange(_activities.Select(activity =>
            (ILiveBufferItem)new SpinnerValue(_activityLabels[activity], _frame)));
        return items;
    }

    private string Name(string agentSessionId)
    {
        if (_names.TryGetValue(agentSessionId, out var name))
        {
            return name;
        }

        _mainSessionId ??= agentSessionId;

        return string.Equals(_mainSessionId, agentSessionId, StringComparison.Ordinal)
            ? "main"
            : agentSessionId;
    }

    private void StartTurn(string agentSessionId)
    {
        var identity = (agentSessionId, AgentActivity);
        if (_activityLabels.ContainsKey(identity))
        {
            return;
        }

        _activities.Add(identity);
        _activityLabels.Add(identity, AgentLabel(agentSessionId));
    }

    private string AgentLabel(string agentSessionId) =>
        _statistics.TryGetValue(agentSessionId, out var statistics)
            ? $"agent {Name(agentSessionId)} ({TokenCount(statistics.InputTokens)} in / " +
              $"{TokenCount(statistics.CachedInputTokens)} cached / {TokenCount(statistics.OutputTokens)} out, " +
              $"{TokenCount(statistics.ContextSize)}/{ContextLimit(statistics.ContextLimit)} ctx)"
            : $"agent {Name(agentSessionId)}";

    private void UpdateAgentLabel(string agentSessionId)
    {
        var identity = (agentSessionId, AgentActivity);
        if (_activityLabels.ContainsKey(identity))
        {
            _activityLabels[identity] = AgentLabel(agentSessionId);
        }
    }

    private async Task FinishTurn(Event published, bool failed, CancellationToken cancellationToken)
    {
        var identity = (published.AgentSessionId, AgentActivity);
        if (!_activityLabels.Remove(identity))
        {
            return;
        }

        _ = _activities.Remove(identity);
        _ = _completedTurns.Add(published.AgentSessionId);
        var name = Name(published.AgentSessionId);
        var interrupted = !failed
            && string.Equals(published.TurnEnded.FinishReason, "interrupted", StringComparison.Ordinal);
        var line = failed
            ? $"! agent {name}: {TerminalText.Sanitize(published.TurnFailed.Message)}"
            : interrupted
                ? $"- agent {name} interrupted"
                : $"+ agent {name} finished";
        await commit(ImmediateScrollbackValue.Muted([line]), Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAgent(Event published, bool failed, CancellationToken cancellationToken)
    {
        if (_completedTurns.Remove(published.AgentSessionId))
        {
            return;
        }

        var identity = (published.AgentSessionId, AgentActivity);
        _ = _activityLabels.Remove(identity);
        _ = _activities.Remove(identity);
        var name = failed ? published.AgentFailed.Name : published.AgentFinished.Name;
        var line = failed
            ? $"! agent {name}: {TerminalText.Sanitize(published.AgentFailed.Message)}"
            : $"+ agent {name} finished";
        await commit(ImmediateScrollbackValue.Muted([line]), Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    private void ToolCall(string agentSessionId, ToolCallChunk chunk)
    {
        var identity = (agentSessionId, chunk.ToolCallId);
        if (!_toolCalls.TryGetValue(identity, out var toolCall))
        {
            toolCall = (chunk.ToolName, new StringBuilder());
        }
        else if (chunk.ToolName.Length > 0)
        {
            toolCall.Name = chunk.ToolName;
        }

        _ = toolCall.Arguments.Append(chunk.ArgumentsFragment);
        _toolCalls[identity] = toolCall;
    }

    private void StartTool(Event published)
    {
        var tool = published.ToolStarted;
        var toolIdentity = (published.AgentSessionId, tool.ToolCallId);
        if (!_toolCalls.TryGetValue(toolIdentity, out var toolCall))
        {
            toolCall = (tool.ToolName, new StringBuilder());
            _toolCalls.Add(toolIdentity, toolCall);
        }
        else if (tool.ToolName.Length > 0)
        {
            toolCall.Name = tool.ToolName;
            _toolCalls[toolIdentity] = toolCall;
        }

        var identity = (published.AgentSessionId, ActivityId(tool.ToolCallId));
        if (_activityLabels.ContainsKey(identity))
        {
            return;
        }

        _activities.Add(identity);
        _activityLabels.Add(identity, $"{Name(published.AgentSessionId)}: {toolCall.Name}");
    }

    private async Task FinishTool(Event published, CancellationToken cancellationToken)
    {
        var (toolCallId, toolName) = TerminalTool(published);
        var toolIdentity = (published.AgentSessionId, toolCallId);
        if (!_toolCalls.Remove(toolIdentity, out var toolCall))
        {
            toolCall = (toolName, new StringBuilder());
        }

        var identity = (published.AgentSessionId, ActivityId(toolCallId));
        _ = _activityLabels.Remove(identity);
        _ = _activities.Remove(identity);
        var description = ToolCallDescription(toolCall);
        var owner = Name(published.AgentSessionId);
        var line = published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolFinished => $"+ {owner}: {description}",
            Event.PayloadOneofCase.ToolCancelled => $"- {owner}: {description} cancelled",
            Event.PayloadOneofCase.ToolError =>
                $"! {owner}: {description}: {TerminalText.Sanitize(published.ToolError.Message)}",
            _ => string.Empty,
        };
        await commit(ImmediateScrollbackValue.Muted([line]), Snapshot(), cancellationToken).ConfigureAwait(false);
    }
}

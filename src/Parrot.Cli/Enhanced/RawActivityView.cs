using System.Text;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RawActivityView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit) : IDisposable
{
    private const int SpinnerIntervalMilliseconds = 80;

    private readonly StringBuilder _reasoning = new();
    private readonly Dictionary<string, (string Name, StringBuilder Arguments)> _toolCalls = [];
    private readonly SemaphoreSlim _rendering = new(1, 1);
    private IReadOnlyList<string> _rows = [];
    private string? _activeToolCallId;
    private bool _started;

    public void Dispose() => _rendering.Dispose();

    public async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            for (var frame = 0; ; frame++)
            {
                await Task.Delay(SpinnerIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (_activeToolCallId is not null
                        && _toolCalls.TryGetValue(_activeToolCallId, out var toolCall))
                    {
                        await draw(
                            [new SpinnerValue(FormatToolCall(toolCall), frame)],
                            cancellationToken).ConfigureAwait(false);
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

    public async Task Prepare(Event published, CancellationToken cancellationToken)
    {
        if (published.PayloadCase is not (Event.PayloadOneofCase.TextChunk or
            Event.PayloadOneofCase.TurnEnded or
            Event.PayloadOneofCase.TurnFailed))
        {
            return;
        }

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await Flush(false, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    public async Task Render(Event published, CancellationToken cancellationToken)
    {
        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _started |= published.PayloadCase == Event.PayloadOneofCase.TurnStarted;
            if (published.PayloadCase is
                Event.PayloadOneofCase.TextChunk or
                Event.PayloadOneofCase.TurnEnded or
                Event.PayloadOneofCase.TurnFailed)
            {
                return;
            }

            var activity = published.PayloadCase switch
            {
                Event.PayloadOneofCase.ReasoningChunk => Reasoning(published.ReasoningChunk.Fragment),
                Event.PayloadOneofCase.ToolCallChunk => ToolCall(published.ToolCallChunk),
                _ => EnhancedActivity.Format(published, _started),
            };
            _rows = Rows(published, activity);
            if (IsTerminalToolEvent(published))
            {
                await Flush(true, cancellationToken).ConfigureAwait(false);
                RemoveToolCall(published);
                return;
            }

            SpinnerValue? spinner = published.PayloadCase == Event.PayloadOneofCase.ToolCallChunk
                ? new SpinnerValue(activity, 0)
                : null;
            if (spinner is not null)
            {
                _rows = [];
            }

            var items = spinner is { } current
                ? [(ILiveBufferItem)current]
                : _rows.Select(value => (ILiveBufferItem)new LiveTextValue(value)).ToList();
            await draw(items, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    private static string FormatToolCall((string Name, StringBuilder Arguments) toolCall) =>
        $"tool call {TerminalText.Sanitize(toolCall.Name)}: {TerminalText.Sanitize(toolCall.Arguments.ToString())}";

    private static bool IsTerminalToolEvent(Event published) =>
        published.PayloadCase is Event.PayloadOneofCase.ToolFinished or
            Event.PayloadOneofCase.ToolCancelled or
            Event.PayloadOneofCase.ToolError;

    private static string? TerminalToolCallId(Event published) => published.PayloadCase switch
    {
        Event.PayloadOneofCase.ToolFinished => published.ToolFinished.ToolCallId,
        Event.PayloadOneofCase.ToolCancelled => published.ToolCancelled.ToolCallId,
        Event.PayloadOneofCase.ToolError => published.ToolError.ToolCallId,
        _ => null,
    };

    private string Reasoning(string fragment)
    {
        _ = _reasoning.Append(TerminalText.Sanitize(fragment));
        return _reasoning.ToString();
    }

    private IReadOnlyList<string> Rows(Event published, string activity)
    {
        var toolCallId = TerminalToolCallId(published);
        if (toolCallId is not null && _toolCalls.TryGetValue(toolCallId, out var toolCall))
        {
            return published.PayloadCase switch
            {
                Event.PayloadOneofCase.ToolFinished => [$"+ {FormatToolCall(toolCall)}"],
                Event.PayloadOneofCase.ToolCancelled => [$"- {FormatToolCall(toolCall)} cancelled"],
                Event.PayloadOneofCase.ToolError =>
                    [$"! {FormatToolCall(toolCall)}: {TerminalText.Sanitize(published.ToolError.Message)}"],
                _ => [FormatToolCall(toolCall)],
            };
        }

        return activity.Length == 0 ? [] : [activity];
    }

    private string ToolCall(ToolCallChunk chunk)
    {
        if (!_toolCalls.TryGetValue(chunk.ToolCallId, out var toolCall))
        {
            toolCall = (chunk.ToolName, new StringBuilder());
        }
        else if (chunk.ToolName.Length > 0)
        {
            toolCall.Name = chunk.ToolName;
        }

        _ = toolCall.Arguments.Append(chunk.ArgumentsFragment);
        _toolCalls[chunk.ToolCallId] = toolCall;
        _activeToolCallId = chunk.ToolCallId;
        return FormatToolCall(toolCall);
    }

    private void RemoveToolCall(Event published)
    {
        var toolCallId = TerminalToolCallId(published);
        if (toolCallId is null)
        {
            return;
        }

        _ = _toolCalls.Remove(toolCallId);
        if (string.Equals(_activeToolCallId, toolCallId, StringComparison.Ordinal))
        {
            _activeToolCallId = null;
        }
    }

    private async Task Flush(bool redraw, CancellationToken cancellationToken)
    {
        var activities = _rows;
        _rows = [];
        var items = redraw
            ? _rows.Select(value => (ILiveBufferItem)new LiveTextValue(value)).ToList()
            : [];
        if (activities.Count == 0)
        {
            await draw(items, cancellationToken).ConfigureAwait(false);
            return;
        }

        await commit(ImmediateScrollbackValue.Muted(activities), items, cancellationToken).ConfigureAwait(false);
    }
}

using System.Text;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedTurnView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    TextWriter error,
    Func<int> columns,
    bool renderActivityEvents,
    bool color,
    ForegroundTurn foreground)
{
    private const string Dim = "\u001b[2m";
    private const string Cyan = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Red = "\u001b[31m";
    private const string Reset = "\u001b[0m";

    private readonly MarkdownLiveRenderer _live = new(columns, color);
    private readonly StringBuilder _reasoning = new();
    private MarkdownLiveUpdate? _pendingTextCompletion;
    private MarkdownLiveUpdate? _pendingTextUpdate;
    private bool _started;
    private bool _textActive;
    private int _textSegment;

    private string TextId => $"assistant-{_textSegment}";

    public async Task Prepare(Event published, CancellationToken cancellationToken)
    {
        if (_textActive && published.PayloadCase != Event.PayloadOneofCase.TextChunk)
        {
            await CommitText(cancellationToken).ConfigureAwait(false);
        }

        if (_reasoning.Length > 0 && published.PayloadCase != Event.PayloadOneofCase.ReasoningChunk)
        {
            await EndReasoning(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool?> Render(Event published, CancellationToken cancellationToken)
    {
        foreground.Observe(published);

        switch (published.PayloadCase)
        {
            case Event.PayloadOneofCase.TurnStarted:
                _started = true;
                if (renderActivityEvents)
                {
                    await RenderActivity(published, cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.None:
            case Event.PayloadOneofCase.InputAdmitted:
            case Event.PayloadOneofCase.InputPromoted:
            case Event.PayloadOneofCase.ToolCallChunk:
            case Event.PayloadOneofCase.RetryNotice:
            case Event.PayloadOneofCase.ToolStarted:
            case Event.PayloadOneofCase.ToolFinished:
            case Event.PayloadOneofCase.ToolCancelled:
            case Event.PayloadOneofCase.ToolError:
            case Event.PayloadOneofCase.AgentStarted:
            case Event.PayloadOneofCase.AgentFinished:
            case Event.PayloadOneofCase.AgentFailed:
                if (renderActivityEvents)
                {
                    await RenderActivity(published, cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.StatusInjected:
                await Commit(
                    ImmediateScrollbackValue.Trusted([$"{Dim}  ↻ Status prompt injected{Reset}"]),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.ReasoningChunk:
                if (renderActivityEvents)
                {
                    await RenderReasoning(published.ReasoningChunk.Fragment, cancellationToken)
                        .ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.TextChunk:
                _textActive = true;
                _pendingTextUpdate = _live.Append(
                    new LiveTerminalStreamMessage(TextId, string.Empty, published.TextChunk.Fragment));
                await Apply(_pendingTextUpdate.Value, cancellationToken).ConfigureAwait(false);
                _pendingTextUpdate = null;
                break;

            case Event.PayloadOneofCase.TurnEnded:
                if (renderActivityEvents)
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Green}  {Summarise(published.TurnEnded)}{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                return foreground.IsTerminal(published) ? true : null;

            case Event.PayloadOneofCase.TurnFailed:
                if (foreground.IsTerminal(published))
                {
                    await error.WriteLineAsync(
                        $"{Red}  {TerminalText.Sanitize(published.TurnFailed.Message)}{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    return false;
                }

                return null;

            default:
                break;
        }

        return null;
    }

    public async Task End(CancellationToken cancellationToken)
    {
        if (_textActive)
        {
            await CommitText(cancellationToken).ConfigureAwait(false);
        }

        if (_reasoning.Length > 0)
        {
            await EndReasoning(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task Cancel(CancellationToken cancellationToken)
    {
        if (_textActive)
        {
            if (_pendingTextUpdate is { } update)
            {
                await Apply(update, CancellationToken.None).ConfigureAwait(false);
                _pendingTextUpdate = null;
            }

            await CommitText(CancellationToken.None).ConfigureAwait(false);
            await draw([], cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Summarise(TurnEnded ended) =>
        $"{TerminalText.Sanitize(ended.FinishReason)} - {ended.InputTokens} in / {ended.OutputTokens} out";

    private Task RenderActivity(Event published, CancellationToken cancellationToken)
    {
        var style = published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolStarted or
            Event.PayloadOneofCase.AgentStarted => Cyan,
            Event.PayloadOneofCase.ToolFinished or
            Event.PayloadOneofCase.AgentFinished => Green,
            Event.PayloadOneofCase.ToolError or
            Event.PayloadOneofCase.AgentFailed => Red,
            _ => Dim,
        };
        return Commit(
            ImmediateScrollbackValue.Trusted([$"{style}  {EnhancedActivity.Format(published, _started)}{Reset}"]),
            cancellationToken);
    }

    private Task Apply(MarkdownLiveUpdate update, CancellationToken cancellationToken)
    {
        var items = update.Preview.Select(value => (ILiveBufferItem)new LiveTextValue(value)).ToList();
        return update.Scrollback is { } scrollback
            ? commit(scrollback, items, cancellationToken)
            : draw(items, cancellationToken);
    }

    private Task Commit(IScrollbackItem item, CancellationToken cancellationToken) =>
        commit(item, [], cancellationToken);

    private async Task CommitText(CancellationToken cancellationToken)
    {
        _pendingTextCompletion ??= _live.Commit();
        await Apply(_pendingTextCompletion.Value, cancellationToken).ConfigureAwait(false);
        _pendingTextCompletion = null;
        _textActive = false;
        _textSegment++;
    }

    private Task RenderReasoning(string fragment, CancellationToken cancellationToken)
    {
        _ = _reasoning.Append(TerminalText.Sanitize(fragment));
        return draw([new LiveTextValue(_reasoning.ToString())], cancellationToken);
    }

    private async Task EndReasoning(CancellationToken cancellationToken)
    {
        await Commit(
            ImmediateScrollbackValue.Trusted([$"{Dim}{_reasoning}{Reset}"]),
            cancellationToken).ConfigureAwait(false);
        _ = _reasoning.Clear();
    }
}

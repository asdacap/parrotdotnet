using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedTurnView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    TextWriter error,
    Func<int> columns,
    bool color,
    ForegroundTurn foreground)
{
    private const string Dim = "\u001b[2m";
    private const string Red = "\u001b[31m";
    private const string Reset = "\u001b[0m";

    private readonly MarkdownLiveRenderer _live = new(columns, color);
    private MarkdownLiveUpdate? _pendingTextCompletion;
    private bool _textActive;
    private string? _textOwner;
    private int _textSegment;

    private string TextId => $"assistant-{_textSegment}";

    public async Task FlushResponse(string owner, CancellationToken cancellationToken)
    {
        if (_textActive && string.Equals(_textOwner, owner, StringComparison.Ordinal))
        {
            await CommitText(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool?> Render(Event published, CancellationToken cancellationToken)
    {
        foreground.Observe(published);

        switch (published.PayloadCase)
        {
            case Event.PayloadOneofCase.RetryNotice:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted(
                            [$"{Dim}  retry {published.RetryNotice.Attempt} in {published.RetryNotice.RetryAfterMs} ms: {TerminalText.Sanitize(published.RetryNotice.Reason)}{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.AgentNotice when published.AgentNotice.Notice.Length > 0:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ {TerminalText.Sanitize(published.AgentNotice.Notice)}{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.SkillLoaded:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted(
                            [$"{Dim}↻ Skill loaded: {TerminalText.Sanitize(published.SkillLoaded.Path)}{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.TextChunk:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    _textOwner ??= published.AgentSessionId;
                    _textActive = true;
                    var update = _live.Append(
                        new LiveTerminalStreamMessage(
                            TextId,
                            TerminalIcons.AssistantMessage + " ",
                            published.TextChunk.Fragment));
                    await Apply(update, cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.ModeTurnCompleted:
                return foreground.IsTerminal(published) ? true : null;

            case Event.PayloadOneofCase.TurnFailed:
                if (foreground.IsMain(published.AgentSessionId))
                {
                    await error.WriteLineAsync(
                        $"{Red}  {TerminalText.Sanitize(published.TurnFailed.Message)}{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                }

                if (published.TurnFailed.ProviderResponseBody.Length > 0)
                {
                    await error.WriteLineAsync(
                        $"{Red}  provider response:{Reset}".AsMemory(), cancellationToken).ConfigureAwait(false);
                    await error.WriteLineAsync(
                        TerminalText.Sanitize(published.TurnFailed.ProviderResponseBody).AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                }

                return foreground.IsTerminal(published) ? false : null;

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
    }

    public async Task Cancel(CancellationToken cancellationToken)
    {
        if (_textActive)
        {
            await CommitText(CancellationToken.None).ConfigureAwait(false);
            await replace([], cancellationToken).ConfigureAwait(false);
        }
    }

    private static IReadOnlyList<ILiveBufferItem> Items(MarkdownLiveUpdate update) =>
        update.Preview.Count == 0
            ? []
            : [new MarqueeValue(update.Prefix, string.Join(' ', update.Preview), 0)];

    private Task Apply(MarkdownLiveUpdate update, CancellationToken cancellationToken) =>
        update.Scrollback is { } scrollback
            ? commit(scrollback, Items(update), cancellationToken)
            : replace(Items(update), cancellationToken);

    private Task Commit(IScrollbackItem item, CancellationToken cancellationToken) =>
        commit(item, [], cancellationToken);

    private async Task CommitText(CancellationToken cancellationToken)
    {
        _pendingTextCompletion ??= _live.Commit();
        await Apply(_pendingTextCompletion.Value, cancellationToken).ConfigureAwait(false);
        _pendingTextCompletion = null;
        _textActive = false;
        _textOwner = null;
        _textSegment++;
    }
}

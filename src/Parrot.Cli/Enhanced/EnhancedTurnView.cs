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
    private int _textSegment;

    private string TextId => $"assistant-{_textSegment}";

    public async Task Prepare(Event published, CancellationToken cancellationToken)
    {
        if (published.PayloadCase == Event.PayloadOneofCase.QueueSnapshot
            || foreground.IsChild(published.AgentSessionId))
        {
            return;
        }

        if (_textActive && published.PayloadCase != Event.PayloadOneofCase.TextChunk)
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

            case Event.PayloadOneofCase.PlanValidationRepairInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Retrying after plan validation failure: {TerminalText.Sanitize(published.PlanValidationRepairInjected.Diagnostic)}{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.PendingChildQuestionReminderInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Retrying with pending child question reminder{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.StatusInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Status prompt injected{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.ActiveWorkReminderInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Active work reminder injected{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.ExitReminderChanged:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ {RawActivityView.ExitReminderNotice(published.ExitReminderChanged)}{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.ExitReminderInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Exit reminder injected{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.ContextReminderInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Context reminder injected ({published.ContextReminderInjected.UsagePercent}% context used){Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.FinalProviderRequestPromptInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Final provider request prompt injected{Reset}"]),
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.ToolAvailabilityRestoredPromptInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Tool availability restored prompt injected{Reset}"]),
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
                if (foreground.IsTerminal(published))
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
        _textSegment++;
    }
}

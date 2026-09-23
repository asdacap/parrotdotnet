using System.Text;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedTurnView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    TextWriter error,
    Func<int> columns,
    bool renderActivityEvents,
    bool color,
    ForegroundTurn foreground,
    ToolPresenterRegistry presenters)
{
    private const string Dim = "\u001b[2m";
    private const string Cyan = "\u001b[36m";
    private const string Green = "\u001b[32m";
    private const string Red = "\u001b[31m";
    private const string Reset = "\u001b[0m";

    private readonly EnhancedActivity _activity = new(presenters);
    private readonly MarkdownLiveRenderer _live = new(columns, color);
    private readonly StringBuilder _reasoning = new();
    private MarkdownLiveUpdate? _pendingTextCompletion;
    private bool _reasoningIsSummary;
    private bool _started;
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

        if (_reasoning.Length > 0 && published.PayloadCase != Event.PayloadOneofCase.ReasoningChunk)
        {
            await EndReasoning(cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<bool?> Render(Event published, CancellationToken cancellationToken)
    {
        foreground.Observe(published);
        _activity.Observe(published);

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
            case Event.PayloadOneofCase.ToolStarted:
            case Event.PayloadOneofCase.ToolFinished:
            case Event.PayloadOneofCase.ToolCancelled:
            case Event.PayloadOneofCase.ToolError:
            case Event.PayloadOneofCase.AgentStarted:
            case Event.PayloadOneofCase.AgentFinished:
            case Event.PayloadOneofCase.AgentFailed:
            case Event.PayloadOneofCase.CompactionStarted:
            case Event.PayloadOneofCase.CompactionFinished:
            case Event.PayloadOneofCase.CompactionFailed:
                if (renderActivityEvents)
                {
                    await RenderActivity(published, cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.RetryNotice:
                await RenderActivity(published, cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.AgentStatisticsUpdated:
            case Event.PayloadOneofCase.QueueSnapshot:
                break;

            case Event.PayloadOneofCase.AgentTaskProgressSnapshot:
                if (renderActivityEvents)
                {
                    await replace(
                        [new AgentTaskProgressLiveValue(published.AgentTaskProgressSnapshot.Clone())],
                        cancellationToken).ConfigureAwait(false);
                }

                break;

            case Event.PayloadOneofCase.PlanValidationRepairInjected:
                await Commit(
                    ImmediateScrollbackValue.Trusted([$"{Dim}↻ Retrying after plan validation failure: {TerminalText.Sanitize(published.PlanValidationRepairInjected.Diagnostic)}{Reset}"]),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.PendingChildQuestionReminderInjected:
                await Commit(
                    ImmediateScrollbackValue.Trusted([$"{Dim}↻ Retrying with pending child question reminder{Reset}"]),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.StatusInjected:
                await Commit(
                    ImmediateScrollbackValue.Trusted([$"{Dim}↻ Status prompt injected{Reset}"]),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.ActiveWorkReminderInjected:
                if (!foreground.IsChild(published.AgentSessionId))
                {
                    await Commit(
                        ImmediateScrollbackValue.Trusted([$"{Dim}↻ Active work reminder injected{Reset}"]),
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
                await Commit(
                    ImmediateScrollbackValue.Trusted([$"{Dim}↻ Final provider request prompt injected{Reset}"]),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.ToolAvailabilityRestoredPromptInjected:
                await Commit(
                    ImmediateScrollbackValue.Trusted([$"{Dim}↻ Tool availability restored prompt injected{Reset}"]),
                    cancellationToken).ConfigureAwait(false);
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

            case Event.PayloadOneofCase.ReasoningChunk:
                if (renderActivityEvents && !foreground.IsChild(published.AgentSessionId))
                {
                    await RenderReasoning(published.ReasoningChunk, cancellationToken).ConfigureAwait(false);
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

            case Event.PayloadOneofCase.TurnEnded:
                if (renderActivityEvents && foreground.IsTerminal(published))
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

        await EndReasoning(cancellationToken).ConfigureAwait(false);
    }

    public async Task Cancel(CancellationToken cancellationToken)
    {
        if (_textActive)
        {
            await CommitText(CancellationToken.None).ConfigureAwait(false);
            await replace([], cancellationToken).ConfigureAwait(false);
        }
    }

    private static string Summarise(TurnEnded ended) =>
        $"{TerminalText.Sanitize(ended.FinishReason)} - {ended.InputTokens} total in / {ended.OutputTokens} total out";

    private static IReadOnlyList<ILiveBufferItem> Items(MarkdownLiveUpdate update) =>
        update.Preview.Count == 0
            ? []
            : [new MarqueeValue(update.Prefix, string.Join(' ', update.Preview), 0)];

    private Task RenderActivity(Event published, CancellationToken cancellationToken)
    {
        var style = published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolStarted or
            Event.PayloadOneofCase.AgentStarted or
            Event.PayloadOneofCase.CompactionStarted => Cyan,
            Event.PayloadOneofCase.ToolFinished or
            Event.PayloadOneofCase.AgentFinished or
            Event.PayloadOneofCase.CompactionFinished => Green,
            Event.PayloadOneofCase.ToolError or
            Event.PayloadOneofCase.AgentFailed or
            Event.PayloadOneofCase.CompactionFailed => Red,
            _ => Dim,
        };
        return Commit(
            ImmediateScrollbackValue.Trusted([$"{style}  {_activity.Format(published, _started)}{Reset}"]),
            cancellationToken);
    }

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

    private async Task RenderReasoning(ReasoningChunk chunk, CancellationToken cancellationToken)
    {
        var summary = chunk.Kind == ReasoningKind.Summary;
        if (_reasoning.Length > 0 && _reasoningIsSummary != summary)
        {
            await EndReasoning(cancellationToken).ConfigureAwait(false);
        }

        _reasoningIsSummary = summary;
        _ = _reasoning.Append(TerminalText.Sanitize(chunk.Fragment));
        await replace([ReasoningItem()], cancellationToken).ConfigureAwait(false);
        if (chunk.Completed)
        {
            await EndReasoning(cancellationToken).ConfigureAwait(false);
        }
    }

    private ILiveBufferItem ReasoningItem() => _reasoningIsSummary
        ? new StreamedResponseValue(TerminalIcons.Reasoning, _reasoning.ToString())
        : new SpinnerValue(_reasoning.ToString(), 0);

    private async Task EndReasoning(CancellationToken cancellationToken)
    {
        if (_reasoning.Length == 0)
        {
            return;
        }

        var reasoning = _reasoning.ToString();
        _ = _reasoning.Clear();
        if (_reasoningIsSummary && reasoning.Trim().Length > 0)
        {
            await Commit(new ReasoningSummaryScrollbackValue(reasoning), cancellationToken).ConfigureAwait(false);
        }
    }
}

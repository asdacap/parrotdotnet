using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedRenderingSession : IDisposable
{
    private readonly EnhancedTurnRenderer _turnRenderer;
    private readonly ToolPresenterRegistry _toolPresenters;
    private readonly TerminalFrameRenderer _renderer;
    private readonly ISlashSession _session;
    private readonly Func<Event, CancellationToken, Task> _observeEvent;
    private readonly Func<CancellationToken, Task> _startMainTurn;
    private readonly Func<CancellationToken, Task> _finishTurn;
    private readonly Func<bool> _isBusy;
    private readonly Func<PlanCompleted, CancellationToken, Task> _completePlan;
    private readonly bool _exitOnFirstCompletion;
    private readonly SemaphoreSlim _composing = new(1, 1);
    private readonly SemaphoreSlim _spinnerLifecycle = new(1, 1);
    private readonly RuntimeUsageTracker _usage = new();
    private readonly ForegroundTurn _foreground = new();
    private readonly HashSet<string> _modelineTools = new(StringComparer.Ordinal);
    private readonly LiveUpdateScheduler _updates;
    private readonly TerminalSpinner _spinner;

    private IReadOnlyList<ILiveBufferItem> _body = [];
    private IReadOnlyList<ILiveBufferItem> _input;
    private string _mainAgentActivity = string.Empty;
    private string _modelineActivity = string.Empty;
    private int _modelineFrame;
    private Task _spinnerRendering = Task.CompletedTask;
    private TaskCompletionSource? _spinnerStop;

    public EnhancedRenderingSession(
        EnhancedTurnRenderer turnRenderer,
        ToolPresenterRegistry toolPresenters,
        TerminalFrameRenderer renderer,
        ISlashSession session,
        IReadOnlyList<ILiveBufferItem> initialInput,
        Func<Event, CancellationToken, Task> observeEvent,
        Func<CancellationToken, Task> startMainTurn,
        Func<CancellationToken, Task> finishTurn,
        Func<bool> isBusy,
        Func<PlanCompleted, CancellationToken, Task> completePlan,
        bool exitOnFirstCompletion)
    {
        ArgumentNullException.ThrowIfNull(turnRenderer);
        ArgumentNullException.ThrowIfNull(toolPresenters);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(session);
        ArgumentNullException.ThrowIfNull(initialInput);
        ArgumentNullException.ThrowIfNull(observeEvent);
        ArgumentNullException.ThrowIfNull(startMainTurn);
        ArgumentNullException.ThrowIfNull(finishTurn);
        ArgumentNullException.ThrowIfNull(isBusy);
        ArgumentNullException.ThrowIfNull(completePlan);

        _turnRenderer = turnRenderer;
        _toolPresenters = toolPresenters;
        _renderer = renderer;
        _session = session;
        _input = [.. initialInput];
        _observeEvent = observeEvent;
        _startMainTurn = startMainTurn;
        _finishTurn = finishTurn;
        _isBusy = isBusy;
        _completePlan = completePlan;
        _exitOnFirstCompletion = exitOnFirstCompletion;
        _updates = new LiveUpdateScheduler(DrawScheduled);
        _spinner = new TerminalSpinner(DrawInitialBody);
    }

    public void Dispose()
    {
        _composing.Dispose();
        _spinnerLifecycle.Dispose();
    }

    internal Task RunUpdates(CancellationToken cancellationToken) => _updates.Run(cancellationToken);

    internal Task<bool> Run(IAsyncStreamReader<Event> stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        return RunCore(new SessionUsageSnapshotStreamReader(stream, ObserveSessionUsage), cancellationToken);
    }

    internal async Task ReplaceInput(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _input = [.. items];
            await DrawFrame(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    internal async Task BeginTurn(
        IScrollbackItem scrollback,
        IReadOnlyList<ILiveBufferItem> input,
        CancellationToken spinnerToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);
        ArgumentNullException.ThrowIfNull(input);

        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _modelineActivity = "Thinking…";
            _input = [.. input];
            await _renderer.Commit(scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }

        await StartSpinner(spinnerToken).ConfigureAwait(false);
    }

    internal async Task Commit(IScrollbackItem scrollback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);

        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _renderer.Commit(scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    internal async Task RefreshReady(CancellationToken cancellationToken)
    {
        if (_isBusy())
        {
            return;
        }

        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_isBusy())
            {
                return;
            }

            _mainAgentActivity = string.Empty;
            _modelineActivity = string.Empty;
            await DrawFrame(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    internal async Task Refresh(CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DrawFrame(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    internal async Task ResetForSession(CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _body = [];
            _usage.Reset();
            _foreground.Reset();
            _modelineTools.Clear();
            _mainAgentActivity = string.Empty;
            _modelineActivity = string.Empty;
            _modelineFrame = 0;
        }
        finally
        {
            _ = _composing.Release();
        }

        _ = _updates.Invalidate();
    }

    internal async Task StopSpinner()
    {
        await _spinnerLifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _ = _spinnerStop?.TrySetResult();
            await _spinnerRendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _spinnerStop = null;
            _spinnerRendering = Task.CompletedTask;
        }
        finally
        {
            _ = _spinnerLifecycle.Release();
        }
    }

    internal async Task Clear(CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _renderer.Clear(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    private IReadOnlyList<ILiveBufferItem> Snapshot() =>
        [.. _body, CreateModeline(), .. _input];

    private ModelineValue CreateModeline()
    {
        var runtime = _usage.Current;
        var right = new[]
        {
            runtime.FormatContext() is { Length: > 0 } context
                ? $"{_session.Model} ({context})"
                : _session.Model,
            runtime.FormatTokens(),
            runtime.FormatCost(),
        };
        var activityLabel = _modelineTools.Count > 0
            ? _modelineActivity
            : _mainAgentActivity.Length > 0
                ? _mainAgentActivity
                : _modelineActivity;
        var activity = activityLabel.Length == 0
            ? string.Empty
            : $"{TerminalIcons.SpinnerFrames[_modelineFrame % TerminalIcons.SpinnerFrames.Length]} {activityLabel}";
        return new ModelineValue(
            _session.Mode,
            activity,
            string.Join(" · ", right.Where(static value => value.Length > 0)));
    }

    private async Task<bool> RunCore(
        IAsyncStreamReader<Event> stream,
        CancellationToken cancellationToken)
    {
        using var activity = new RawActivityView(
            ReplaceBody,
            CommitBody,
            _toolPresenters,
            UpdateMainAgentActivity);
        var queueStream = new QueueSnapshotStreamReader(stream, activity.ReplaceQueues);
        var renderedStream = new ShellProcessSnapshotStreamReader(queueStream, activity.ReplaceProcesses);
        using var animating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = activity.Run(animating.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var failed = false;
                PlanCompleted? plan = null;

                async Task Prepare(Event published, CancellationToken token)
                {
                    _foreground.Observe(published);
                    if (published.PayloadCase == Event.PayloadOneofCase.TurnFailed
                        && _foreground.IsTerminal(published))
                    {
                        failed = true;
                    }
                    else if (published.PayloadCase == Event.PayloadOneofCase.PlanCompleted
                             && _foreground.IsMain(published.AgentSessionId))
                    {
                        plan = published.PlanCompleted;
                    }

                    if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted
                        && _foreground.IsMain(published.AgentSessionId))
                    {
                        await _startMainTurn(token).ConfigureAwait(false);
                    }

                    await ObserveRenderingEvent(published, token).ConfigureAwait(false);
                    await _observeEvent(published, token).ConfigureAwait(false);
                    await StopSpinner().ConfigureAwait(false);
                    await activity.Prepare(published, token).ConfigureAwait(false);
                }

                async Task Render(Event published, CancellationToken token)
                {
                    await activity.Render(published, token).ConfigureAwait(false);
                    _ = _updates.Invalidate();
                }

                var completed = await _turnRenderer.RenderSessionTurn(
                    renderedStream,
                    Prepare,
                    Render,
                    activity.ReplaceContent,
                    activity.CommitContent,
                    _foreground,
                    cancellationToken).ConfigureAwait(false);

                if (!completed && !failed)
                {
                    return false;
                }

                await _finishTurn(cancellationToken).ConfigureAwait(false);
                if (plan is not null)
                {
                    if (plan.Markdown.Length > 0)
                    {
                        await activity.CommitContent(
                            new MarkdownScrollbackValue(plan.Markdown),
                            [],
                            cancellationToken).ConfigureAwait(false);
                    }

                    if (plan.TaskTree is { RootNodes.Count: > 0 })
                    {
                        await activity.CommitContent(
                            new AgentTaskProgressScrollbackValue(plan.TaskTree),
                            [],
                            cancellationToken).ConfigureAwait(false);
                    }

                    await _completePlan(plan, cancellationToken).ConfigureAwait(false);
                }

                if (_exitOnFirstCompletion)
                {
                    return completed;
                }

                await RefreshReady(cancellationToken).ConfigureAwait(false);
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            await animating.CancelAsync().ConfigureAwait(false);
            await animation.ConfigureAwait(false);
        }
    }

    private async Task ObserveRenderingEvent(Event published, CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObserveModelineActivity(published);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    private async Task ObserveSessionUsage(
        SessionUsageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_usage.Observe(snapshot))
            {
                _ = _updates.Invalidate();
            }
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    private void ObserveModelineActivity(Event published)
    {
        if (_foreground.IsTerminal(published)
            || (published.PayloadCase == Event.PayloadOneofCase.TurnStarted
                && _foreground.IsMain(published.AgentSessionId)))
        {
            _modelineTools.Clear();
            _modelineActivity = string.Empty;
        }
        else if (published.PayloadCase == Event.PayloadOneofCase.ToolStarted
                 && _foreground.IsMain(published.AgentSessionId)
                 && _toolPresenters.Describe(published.ToolStarted.ToolName).Modeline)
        {
            _ = _modelineTools.Add(published.ToolStarted.ToolCallId);
            _modelineActivity = $"Working: {published.ToolStarted.ToolName}";
        }
        else if (published.PayloadCase is Event.PayloadOneofCase.ToolFinished
                 or Event.PayloadOneofCase.ToolCancelled
                 or Event.PayloadOneofCase.ToolError)
        {
            var toolCallId = published.PayloadCase switch
            {
                Event.PayloadOneofCase.ToolFinished => published.ToolFinished.ToolCallId,
                Event.PayloadOneofCase.ToolCancelled => published.ToolCancelled.ToolCallId,
                _ => published.ToolError.ToolCallId,
            };
            if (_modelineTools.Remove(toolCallId) && _modelineTools.Count == 0)
            {
                _modelineActivity = string.Empty;
            }
        }
    }

    private async Task UpdateMainAgentActivity(string activity, CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _mainAgentActivity = activity;
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    private async Task ReplaceBody(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _body = [.. items];
            _modelineFrame++;
        }
        finally
        {
            _ = _composing.Release();
        }

        _ = _updates.Invalidate();
    }

    private async Task DrawInitialBody(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _body = [.. items];
            _modelineFrame++;
            await DrawFrame(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    private async Task CommitBody(
        IScrollbackItem scrollback,
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _body = [.. items];
            await _renderer.Commit(scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    private async Task DrawScheduled(CancellationToken cancellationToken)
    {
        await _composing.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DrawFrame(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _composing.Release();
        }
    }

    private Task DrawFrame(CancellationToken cancellationToken) =>
        _renderer.Draw(Snapshot(), cancellationToken);

    private async Task StartSpinner(CancellationToken cancellationToken)
    {
        await _spinnerLifecycle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _ = _spinnerStop?.TrySetResult();
            await _spinnerRendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _spinnerStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
            var stop = _spinnerStop;
            _spinnerRendering = _spinner.Run(
                static index => new SpinnerValue("thinking", index),
                async (preserve, lifetimeToken) =>
                {
                    await stop.Task.WaitAsync(lifetimeToken).ConfigureAwait(false);
                    await preserve().ConfigureAwait(false);
                },
                cancellationToken);
        }
        finally
        {
            _ = _spinnerLifecycle.Release();
        }
    }
}

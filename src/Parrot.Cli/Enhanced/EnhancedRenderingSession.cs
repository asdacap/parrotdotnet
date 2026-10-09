using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedRenderingSession : IAsyncDisposable
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
    private readonly RollingTokenRateWindow _rates;
    private readonly RollingTokenRateRefreshLifecycle _rateRefresh;
    private readonly TimeProvider _timeProvider;
    private readonly ForegroundTurn _foreground = new();
    private readonly HashSet<string> _modelineTools = new(StringComparer.Ordinal);
    private readonly LiveUpdateScheduler _updates;
    private readonly TerminalSpinner _spinner;

    private IReadOnlyList<ILiveBufferItem> _body = [];
    private IReadOnlyList<ILiveBufferItem> _input;
    private long? _questionRemainingSeconds;
    private bool _awaitingQuestionAnswer;
    private string _mainAgentActivity = string.Empty;
    private string _modelineActivity = string.Empty;
    private uint _requestAttempt;
    private bool _waitingForFirstToken;
    private RunningDuration? _rootTurnDuration;
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
        : this(
            turnRenderer,
            toolPresenters,
            renderer,
            session,
            initialInput,
            observeEvent,
            startMainTurn,
            finishTurn,
            isBusy,
            completePlan,
            exitOnFirstCompletion,
            TimeProvider.System,
            static (delay, cancellationToken) => Task.Delay(delay, cancellationToken),
            null)
    {
    }

    internal EnhancedRenderingSession(
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
        bool exitOnFirstCompletion,
        TimeProvider timeProvider,
        Func<TimeSpan, CancellationToken, Task> rateDelay,
        Func<CancellationToken, Task>? rateInvalidate)
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
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(rateDelay);

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
        _timeProvider = timeProvider;
        _updates = new LiveUpdateScheduler(Refresh);
        _rates = new RollingTokenRateWindow(timeProvider);
        _rateRefresh = new RollingTokenRateRefreshLifecycle(
            _rates,
            rateInvalidate ?? InvalidateRate,
            rateDelay);
        _spinner = new TerminalSpinner(DrawInitialBody);
    }

    public async ValueTask DisposeAsync()
    {
        await _rateRefresh.ShutdownAsync().ConfigureAwait(false);
        _composing.Dispose();
        _spinnerLifecycle.Dispose();
    }

    internal Task RunUpdates(CancellationToken cancellationToken) => _updates.Run(cancellationToken);

    internal Task<bool> Run(IAsyncStreamReader<Event> stream, CancellationToken cancellationToken) =>
        RunWithReplay(stream, static () => false, cancellationToken);

    internal Task<bool> RunWithReplay(
        IAsyncStreamReader<Event> stream,
        Func<bool> isReplaying,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(isReplaying);
        return RunCore(new ProviderCallUsageStreamReader(stream, ObserveProviderCallUsage), isReplaying, cancellationToken);
    }

    internal async Task ReplaceInput(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        _input = [.. items];
        await DrawFrame(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task SetAwaitingQuestionAnswer(bool awaitingAnswer, CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        _awaitingQuestionAnswer = awaitingAnswer;
        await DrawFrame(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task UpdateQuestionCountdown(long? remainingSeconds, CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        if (_questionRemainingSeconds == remainingSeconds)
        {
            return;
        }

        _questionRemainingSeconds = remainingSeconds;
        _ = _updates.Invalidate();
    }

    internal async Task BeginTurn(
        IScrollbackItem scrollback,
        IReadOnlyList<ILiveBufferItem> input,
        CancellationToken spinnerToken,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);
        ArgumentNullException.ThrowIfNull(input);

        using (await _composing.Lock(cancellationToken).ConfigureAwait(false))
        {
            _requestAttempt = 0;
            _waitingForFirstToken = false;
            _modelineActivity = "Preparing turn…";
            _input = [.. input];
            await _renderer.Commit(scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
        }

        await StartSpinner(spinnerToken).ConfigureAwait(false);
    }

    internal async Task Commit(IScrollbackItem scrollback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);

        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        await _renderer.Commit(scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task RefreshReady(CancellationToken cancellationToken)
    {
        if (_isBusy())
        {
            return;
        }

        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        if (_isBusy())
        {
            return;
        }

        _mainAgentActivity = string.Empty;
        _requestAttempt = 0;
        _waitingForFirstToken = false;
        _modelineActivity = string.Empty;
        await DrawFrame(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task Refresh(CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        await DrawFrame(CancellationToken.None).ConfigureAwait(false);
    }

    internal async Task ResetForSession(CancellationToken cancellationToken)
    {
        Task rateRefreshReset;
        using (await _composing.Lock(cancellationToken).ConfigureAwait(false))
        {
            rateRefreshReset = _rateRefresh.ResetAsync();
            _body = [];
            _usage.Reset();
            _foreground.Reset();
            _modelineTools.Clear();
            _mainAgentActivity = string.Empty;
            _requestAttempt = 0;
            _waitingForFirstToken = false;
            _modelineActivity = string.Empty;
            _rootTurnDuration = null;
            _modelineFrame = 0;
        }

        await rateRefreshReset.ConfigureAwait(false);
        _ = _updates.Invalidate();
    }

    internal async Task StopSpinner()
    {
        using var spinnerLifecycleLock = await _spinnerLifecycle.Lock(CancellationToken.None).ConfigureAwait(false);
        _ = _spinnerStop?.TrySetResult();
        await _spinnerRendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _spinnerStop = null;
        _spinnerRendering = Task.CompletedTask;
    }

    internal async Task Clear(CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        await _renderer.Clear(CancellationToken.None).ConfigureAwait(false);
    }

    private static async Task RenderPlanReport(
        PlanCompleted plan,
        RawActivityView activity,
        CancellationToken cancellationToken)
    {
        if (plan.Markdown.Length > 0)
        {
            await activity.CommitContent(
                new MarkdownScrollbackValue(plan.Markdown),
                [],
                cancellationToken).ConfigureAwait(false);
        }

        if (plan.TaskDeclarations.Count > 0)
        {
            if (AgentTaskDeclarationFormatter.Format(plan.TaskDeclarations).Count > 0)
            {
                await activity.CommitContent(
                    new AgentTaskDeclarationScrollbackValue(plan.TaskDeclarations),
                    [],
                    cancellationToken).ConfigureAwait(false);
            }
        }
        else if (plan.TaskTree is { RootNodes.Count: > 0 }
            && AgentTaskProgressFormatter.Format(plan.TaskTree).Count > 0)
        {
            await activity.CommitContent(
                new AgentTaskProgressScrollbackValue(plan.TaskTree),
                [],
                cancellationToken).ConfigureAwait(false);
        }
    }

    private Task InvalidateRate(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = _updates.Invalidate();
        return Task.CompletedTask;
    }

    private IReadOnlyList<ILiveBufferItem> Snapshot() =>
        [.. _body, CreateModeline(),
            .. _questionRemainingSeconds is { } remainingSeconds
                ? new ILiveBufferItem[] { new QuestionCountdownValue(remainingSeconds) }
                : [],
            .. _input];

    private ModelineValue CreateModeline()
    {
        var runtime = _usage.Current;
        var right = new[]
        {
            runtime.FormatContext() is { Length: > 0 } context
                ? $"{_session.Model} ({context})"
                : _session.Model,
            runtime.FormatTokens(),
            RuntimeUsage.FormatRate(_rates.Current),
            runtime.FormatCost(),
        };
        var activityLabel = _waitingForFirstToken
            ? "Waiting for first token…"
            : _requestAttempt > 0
                ? _requestAttempt == 1 ? "Requesting…" : $"Requesting (attempt {_requestAttempt})…"
                : _modelineTools.Count > 0
                    ? _modelineActivity
                    : _mainAgentActivity.Length > 0
                        ? _mainAgentActivity
                        : _modelineActivity;
        if (_rootTurnDuration is { } duration && activityLabel.Length > 0)
        {
            activityLabel = $"{activityLabel} (running {duration.Format()})";
        }

        var activity = _awaitingQuestionAnswer
            ? "Waiting for your answer…"
            : activityLabel.Length == 0
                ? string.Empty
                : $"{TerminalIcons.SpinnerFrames[_modelineFrame % TerminalIcons.SpinnerFrames.Length]} {activityLabel}";
        return new ModelineValue(
            _session.Mode,
            activity,
            string.Join(" · ", right.Where(static value => value.Length > 0)));
    }

    private async Task<bool> RunCore(
        IAsyncStreamReader<Event> stream,
        Func<bool> isReplaying,
        CancellationToken cancellationToken)
    {
        await using var activity = new RawActivityView(
            ReplaceBody,
            CommitBody,
            _toolPresenters,
            UpdateMainAgentActivity);
        var preamble = true;
        var usageStream = new SessionUsageSnapshotStreamReader(stream, async (snapshot, token) =>
        {
            if (preamble)
            {
                preamble = false;
                await activity.RetireUnconfirmedProcesses(token).ConfigureAwait(false);
            }

            await ObserveSessionUsage(snapshot, token).ConfigureAwait(false);
        });
        var queueStream = new QueueSnapshotStreamReader(new RootLifecycleFilterStreamReader(usageStream), activity.ReplaceQueues);
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
                    _foreground.ObserveReplay(isReplaying());
                    _foreground.Observe(published);
                    if (published.PayloadCase == Event.PayloadOneofCase.TurnFailed
                        && _foreground.IsTerminal(published))
                    {
                        failed = true;
                    }
                    else if (published.PayloadCase == Event.PayloadOneofCase.PlanCompleted
                             && _foreground.IsMain(published.AgentSessionId))
                    {
                        if (!_foreground.IsReplaying)
                        {
                            plan = published.PlanCompleted;
                        }
                    }

                    if (!_foreground.IsReplaying && published.PayloadCase == Event.PayloadOneofCase.TurnStarted
                        && _foreground.IsMain(published.AgentSessionId))
                    {
                        await _startMainTurn(token).ConfigureAwait(false);
                    }

                    if (!_foreground.IsReplaying)
                    {
                        await ObserveRenderingEvent(published, token).ConfigureAwait(false);
                    }

                    await StopSpinner().ConfigureAwait(false);
                    await activity.Prepare(published, token).ConfigureAwait(false);
                }

                async Task Render(Event published, CancellationToken token)
                {
                    await activity.Render(published, token).ConfigureAwait(false);
                    if (!_foreground.IsReplaying)
                    {
                        await _observeEvent(published, token).ConfigureAwait(false);
                    }

                    if (_foreground.IsReplaying && published.PayloadCase == Event.PayloadOneofCase.PlanCompleted
                        && _foreground.IsMain(published.AgentSessionId))
                    {
                        await RenderPlanReport(published.PlanCompleted, activity, token).ConfigureAwait(false);
                    }

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
                    await RenderPlanReport(plan, activity, cancellationToken).ConfigureAwait(false);

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
            await activity.ResetRequests(CancellationToken.None).ConfigureAwait(false);
            using var composingLock = await _composing.Lock(CancellationToken.None).ConfigureAwait(false);
            _requestAttempt = 0;
            _waitingForFirstToken = false;
            _ = _updates.Invalidate();
        }
    }

    private async Task ObserveRenderingEvent(Event published, CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        ObserveModelineActivity(published);
    }

    private async Task ObserveProviderCallUsage(
        ProviderCallUsage usage,
        CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        _rates.Observe(usage.InputTokens, usage.OutputTokens);
        _rateRefresh.EnsureRefreshing(cancellationToken);
        _ = _updates.Invalidate();
    }

    private async Task ObserveSessionUsage(
        SessionUsageSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        if (_usage.Observe(snapshot))
        {
            _ = _updates.Invalidate();
        }
    }

    private void ObserveModelineActivity(Event published)
    {
        if (_foreground.IsTerminal(published))
        {
            _rootTurnDuration = null;
            _modelineTools.Clear();
            _requestAttempt = 0;
            _waitingForFirstToken = false;
            _modelineActivity = string.Empty;
        }
        else if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted
                 && _foreground.IsMain(published.AgentSessionId))
        {
            _rootTurnDuration = new RunningDuration(_timeProvider);
            _modelineTools.Clear();
            _requestAttempt = 0;
            _waitingForFirstToken = false;
            _modelineActivity = string.Empty;
        }
        else if (published.PayloadCase == Event.PayloadOneofCase.ProviderRequestPhaseChanged
                 && _foreground.IsMain(published.AgentSessionId))
        {
            _waitingForFirstToken = published.ProviderRequestPhaseChanged.Phase == ProviderRequestPhase.HeadersReceived;
            _requestAttempt = published.ProviderRequestPhaseChanged.Phase == ProviderRequestPhase.Requesting
                ? Math.Max(1u, published.ProviderRequestPhaseChanged.Attempt)
                : 0;
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
            if (_modelineTools.Remove(TerminalToolEvent.ReadTool(published).ToolCallId) && _modelineTools.Count == 0)
            {
                _modelineActivity = string.Empty;
            }
        }
    }

    private async Task UpdateMainAgentActivity(string activity, CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        _mainAgentActivity = activity;
    }

    private async Task ReplaceBody(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        using (await _composing.Lock(cancellationToken).ConfigureAwait(false))
        {
            _body = items;
            _modelineFrame++;
        }

        _ = _updates.Invalidate();
    }

    private async Task DrawInitialBody(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        _body = items;
        _modelineFrame++;
        await DrawFrame(CancellationToken.None).ConfigureAwait(false);
    }

    private async Task CommitBody(
        IScrollbackItem scrollback,
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        using var composingLock = await _composing.Lock(cancellationToken).ConfigureAwait(false);
        _body = items;
        await _renderer.Commit(scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
    }

    private Task DrawFrame(CancellationToken cancellationToken) =>
        _renderer.Draw(Snapshot(), cancellationToken);

    private async Task StartSpinner(CancellationToken cancellationToken)
    {
        using var spinnerLifecycleLock = await _spinnerLifecycle.Lock(cancellationToken).ConfigureAwait(false);
        _ = _spinnerStop?.TrySetResult();
        await _spinnerRendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _spinnerStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var stop = _spinnerStop;
        _spinnerRendering = _spinner.Run(
            static index => new SpinnerValue("preparing turn", index),
            async (preserve, lifetimeToken) =>
            {
                await stop.Task.WaitAsync(lifetimeToken).ConfigureAwait(false);
                await preserve().ConfigureAwait(false);
            },
            cancellationToken);
    }
}

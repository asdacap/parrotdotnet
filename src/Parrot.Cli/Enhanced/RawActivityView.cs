using System.Runtime.ExceptionServices;
using System.Text.Json;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RawActivityView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    Func<CancellationToken, Task> delay,
    Func<TimeSpan, CancellationToken, Task> progressDelay,
    ToolPresenterRegistry presenters,
    Func<string, CancellationToken, Task> updateMainAgentActivity) : IAsyncDisposable
{
    private const int SpinnerIntervalMilliseconds = 80;
    private static readonly TimeSpan ProgressQuietPeriod = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan StartGracePeriod = TimeSpan.FromSeconds(2);

    private readonly List<(AgentSessionState State, string ActivityId)> _activities = [];
    private readonly Dictionary<string, AgentSessionState> _agentSessions = new(StringComparer.Ordinal);
    private readonly AgentSessionHierarchy _hierarchy = new();
    private readonly Dictionary<string, ProcessState> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string OwnerAgentSessionId, string ProcessId), CompletedShellProcess> _processCompletions = [];
    private readonly Dictionary<(string OwnerAgentSessionId, string Name), ILiveBufferItem> _queues = [];
    private readonly HashSet<(string OwnerAgentSessionId, string ProcessKey)> _completedProcesses = [];
    private readonly HashSet<(string OwnerAgentSessionId, string ToolCallId)> _omittedProcessTools = [];
    private readonly HashSet<(string OwnerAgentSessionId, string ToolCallId)> _terminalProcessTools = [];
    private readonly Dictionary<string, InventoryState> _processInventories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, InventoryState> _queueInventories = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingProgress> _pendingProgress = new(StringComparer.Ordinal);
    private readonly Dictionary<(string AgentSessionId, string ToolCallId), PendingStart> _pendingStarts = [];
    private readonly HashSet<Task> _progressTasks = [];
    private readonly object _progressTasksLock = new();
    private readonly object _shutdownLock = new();
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    private readonly SemaphoreSlim _rendering = new(1, 1);

    private bool _progressShutdown;
    private bool _disposed;
    private bool _progressFailureReported;
    private ExceptionDispatchInfo? _progressFailure;
    private Task? _shutdownTask;

    private IReadOnlyList<ILiveBufferItem> _content = [];
    private int _frame;

    public RawActivityView(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        ToolPresenterRegistry presenters,
        Func<string, CancellationToken, Task> updateMainAgentActivity)
        : this(
            replace,
            commit,
            static cancellationToken => Task.Delay(SpinnerIntervalMilliseconds, cancellationToken),
            static (quietPeriod, cancellationToken) => Task.Delay(quietPeriod, cancellationToken),
            presenters,
            updateMainAgentActivity)
    {
    }

    internal RawActivityView(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        Func<CancellationToken, Task> delay,
        ToolPresenterRegistry presenters,
        Func<string, CancellationToken, Task> updateMainAgentActivity)
        : this(
            replace,
            commit,
            delay,
            static (quietPeriod, cancellationToken) => Task.Delay(quietPeriod, cancellationToken),
            presenters,
            updateMainAgentActivity)
    {
    }

    public async ValueTask DisposeAsync()
    {
        var failureAlreadyReported = _progressFailureReported;
        try
        {
            await Shutdown().ConfigureAwait(false);
        }
        catch when (failureAlreadyReported)
        {
        }
    }

    public async Task ResetRequests(CancellationToken cancellationToken)
    {
        using var renderingLock = await _rendering.Lock(cancellationToken).ConfigureAwait(false);
        foreach (var state in _agentSessions.Values)
        {
            state.ObserveRequestPhase(new ProviderRequestPhaseChangedEvent());
        }

        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    public async Task Run(CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                await delay(cancellationToken).ConfigureAwait(false);
                using var renderingLock = await _rendering.Lock(cancellationToken).ConfigureAwait(false);
                _frame++;
                if (_activities.Count > 0 || _processes.Count > 0)
                {
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async Task ReplaceContent(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        using var renderingLock = await _rendering.Lock(cancellationToken).ConfigureAwait(false);
        _content = Capture(items);
        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    public async Task CommitContent(
        IScrollbackItem scrollback,
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scrollback);
        ArgumentNullException.ThrowIfNull(items);

        using var renderingLock = await _rendering.Lock(cancellationToken).ConfigureAwait(false);
        _content = Capture(items);
        await commit(scrollback, Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    public async Task Prepare(Event published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (published.PayloadCase == Event.PayloadOneofCase.ReasoningChunk
            || !_agentSessions.TryGetValue(published.AgentSessionId, out var state)
            || !state.HasReasoning)
        {
            return;
        }

        using var renderingLock = await _rendering.Lock(cancellationToken).ConfigureAwait(false);
        await CommitReasoning(state, cancellationToken).ConfigureAwait(false);
    }

    public async Task ReplaceProcesses(
        ShellProcessSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        using var renderingLock = await _rendering.Lock(cancellationToken).ConfigureAwait(false);
        var inventory = ObserveInventory(_processInventories, snapshot.OwnerAgentSessionId);
        if (!inventory.Accept(snapshot.InventoryInstanceId, snapshot.Revision, snapshot.Removed, out var replaced))
        {
            return;
        }

        if (replaced)
        {
            ClearOwnerProcesses(snapshot.OwnerAgentSessionId);
        }

        var snapshotProcessIds = snapshot.Processes
            .Select(static process => process.ProcessId)
            .ToHashSet(StringComparer.Ordinal);
        ObserveProcessCompletions(snapshot.OwnerAgentSessionId, snapshot.CompletedProcesses);
        var completions = _processes.Values
            .Where(process => string.Equals(process.Process.OwnerAgentSessionId, snapshot.OwnerAgentSessionId, StringComparison.Ordinal)
                && (snapshot.Removed || process.Revision < snapshot.Revision)
                && !snapshotProcessIds.Contains(process.Process.ProcessId))
            .OrderBy(static process => process.Process.Depth)
            .ThenBy(static process => process.Process.OwnerAgentName, StringComparer.Ordinal)
            .ThenBy(static process => process.Process.Name, StringComparer.Ordinal)
            .ThenBy(static process => process.Process.ProcessId, StringComparer.Ordinal)
            .ToArray();
        var future = _processes.Values
            .Where(process => string.Equals(process.Process.OwnerAgentSessionId, snapshot.OwnerAgentSessionId, StringComparison.Ordinal)
                && !snapshot.Removed && process.Revision >= snapshot.Revision
                && !snapshotProcessIds.Contains(process.Process.ProcessId))
            .ToArray();
        var deferred = _processes.Values
            .Where(static process => process.IsDeferred)
            .ToDictionary(static process => process.Process.ProcessId, StringComparer.Ordinal);
        foreach (var processId in _processes.Where(process => string.Equals(
            process.Value.Process.OwnerAgentSessionId, snapshot.OwnerAgentSessionId, StringComparison.Ordinal))
            .Select(static process => process.Key).ToArray())
        {
            _ = _processes.Remove(processId);
        }

        foreach (var process in snapshot.Processes)
        {
            ObserveOwnerHierarchy(process.OwnerAgentSessionId, process.OwnerAgentName, process.ParentAgentSessionId, process.ParentAgentName);
            if (deferred.TryGetValue(process.ProcessId, out var pending))
            {
                _processes.Add(process.ProcessId, pending.Observe(process, snapshot.Revision, _timeProvider.GetTimestamp()));
            }
            else
            {
                _processes.Add(process.ProcessId, ProcessState.Observe(
                    process, snapshot.InventoryInstanceId, snapshot.Revision, _timeProvider.GetTimestamp()));
            }
        }

        if (snapshot.Processes.Count > 0)
        {
            RefreshToolPresentations();
        }

        foreach (var process in future)
        {
            _processes[process.Process.ProcessId] = process;
        }

        var committed = false;
        foreach (var completion in completions)
        {
            var originTool = OriginTool(completion.Process);
            if (originTool is { } origin && IsOriginToolActive(completion.Process))
            {
                _ = _omittedProcessTools.Add(origin);
            }
            else if ((originTool is not { } terminalOrigin || !_terminalProcessTools.Remove(terminalOrigin))
                && !string.IsNullOrWhiteSpace(completion.Command)
                && _completedProcesses.Add((completion.Process.OwnerAgentSessionId, ProcessKey(completion.InventoryInstanceId, completion.Process.ProcessId))))
            {
                committed = true;
                await commit(
                    WrapProcess(
                        completion.Process,
                        ProcessCompletion(
                            ExecCommandToolPresenter.Summarize(completion.Process.Name, completion.Process.Description, completion.Command),
                            completion.Process.OwnerAgentSessionId,
                            completion.Process.ProcessId)),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        if (!committed)
        {
            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ReplaceQueues(
        QueueSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        using var renderingLock = await _rendering.Lock(cancellationToken).ConfigureAwait(false);
        var inventory = ObserveInventory(_queueInventories, snapshot.OwnerAgentSessionId);
        if (!inventory.Accept(snapshot.InventoryInstanceId, snapshot.Revision, snapshot.Removed, out _))
        {
            return;
        }

        _hierarchy.ObserveRoot(snapshot.RootAgentSessionId);
        foreach (var key in _queues.Keys.Where(key => string.Equals(
            key.OwnerAgentSessionId, snapshot.OwnerAgentSessionId, StringComparison.Ordinal)).ToArray())
        {
            _ = _queues.Remove(key);
        }

        foreach (var queue in snapshot.Queues.Where(static queue => queue.Name.Length > 0 && queue.ItemCount > 0))
        {
            ObserveOwnerHierarchy(queue.OwnerAgentSessionId, queue.OwnerAgentName, queue.ParentAgentSessionId, queue.ParentAgentName);
            var key = (queue.OwnerAgentSessionId, queue.Name);
            _queues[key] = new QueueLiveBufferItem(queue.Name, queue.Description, queue.ItemCount)
            {
                OwnerAgentSessionId = queue.OwnerAgentSessionId,
            };
        }

        RefreshToolPresentations();
        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    public async Task Render(Event published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (published.PayloadCase == Event.PayloadOneofCase.QueueSnapshot)
        {
            return;
        }

        using var renderingLock = await _rendering.Lock(cancellationToken).ConfigureAwait(false);
        var rootSessionId = _hierarchy.RootSessionId;
        _hierarchy.Observe(published);
        if (!string.Equals(rootSessionId, _hierarchy.RootSessionId, StringComparison.Ordinal)
            || published.PayloadCase is Event.PayloadOneofCase.AgentStarted
            or Event.PayloadOneofCase.AgentFinished
            or Event.PayloadOneofCase.AgentFailed)
        {
            RefreshToolPresentations();
        }

        switch (published.PayloadCase)
        {
            case Event.PayloadOneofCase.AgentStarted:
                await UpdateAgentName(
                    published.AgentSessionId,
                    published.AgentStarted.Name,
                    cancellationToken).ConfigureAwait(false);
                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.AgentStatisticsUpdated:
                await UpdateAgentStatistics(
                    published.AgentSessionId,
                    published.AgentStatisticsUpdated,
                    cancellationToken).ConfigureAwait(false);
                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.AgentFinished:
                await FinishAgent(published, failed: false, cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.AgentFailed:
                await FinishAgent(published, failed: true, cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.TurnStarted:
                await StartTurn(
                    published.AgentSessionId,
                    LiveModelAliasIcon.Convert(published.TurnStarted.ModelAliasIcon),
                    cancellationToken).ConfigureAwait(false);
                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.TurnEnded:
                await FinishTurn(published, failed: false, cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.TurnFailed:
                await FinishTurn(published, failed: true, cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.CompactionStarted:
                StartCompaction(published.AgentSessionId);
                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.CompactionFinished:
            case Event.PayloadOneofCase.CompactionFailed:
                await FinishCompaction(published, cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.ProviderRequestPhaseChanged:
                GetAgentSession(published.AgentSessionId).ObserveRequestPhase(
                    published.ProviderRequestPhaseChanged);
                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.ToolCallChunk:
                ToolCall(published.AgentSessionId, published.ToolCallChunk);
                break;
            case Event.PayloadOneofCase.TextChunk when _hierarchy.IsChild(published.AgentSessionId):
                GetNamedAgentSession(published.AgentSessionId).CollectResponse(published.TextChunk.Fragment);
                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.ToolStarted:
                await StartTool(published, cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.ToolFinished:
            case Event.PayloadOneofCase.ToolCancelled:
            case Event.PayloadOneofCase.ToolError:
                await FinishTool(published, cancellationToken).ConfigureAwait(false);
                break;
            case Event.PayloadOneofCase.AgentTaskProgressSnapshot:
            {
                var state = GetAgentSession(published.AgentSessionId);
                if (!_progressShutdown && state.OfferAgentTaskProgress(published.AgentTaskProgressSnapshot))
                {
                    ScheduleProgress(state, published.AgentTaskProgressSnapshot);
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            case Event.PayloadOneofCase.ActiveWorkReminderInjected when _hierarchy.IsChild(published.AgentSessionId):
                await commit(
                    Wrap(
                        GetNamedAgentSession(published.AgentSessionId),
                        new ActivityNoticeScrollbackValue(TerminalIcons.StatusNotice, "Active work reminder injected")),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.ExitReminderChanged when _hierarchy.IsChild(published.AgentSessionId):
                await commit(
                    Wrap(
                        GetNamedAgentSession(published.AgentSessionId),
                        new ActivityNoticeScrollbackValue(
                            TerminalIcons.StatusNotice,
                            ExitReminderNotice(published.ExitReminderChanged))),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.ExitReminderInjected when _hierarchy.IsChild(published.AgentSessionId):
                await commit(
                    Wrap(
                        GetNamedAgentSession(published.AgentSessionId),
                        new ActivityNoticeScrollbackValue(TerminalIcons.StatusNotice, "Exit reminder injected")),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.SkillLoaded when _hierarchy.IsChild(published.AgentSessionId):
                await commit(
                    Wrap(
                        GetNamedAgentSession(published.AgentSessionId),
                        new ActivityNoticeScrollbackValue(
                            TerminalIcons.StatusNotice,
                            $"Skill loaded: {published.SkillLoaded.Path}")),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.RetryNotice
                or Event.PayloadOneofCase.StatusInjected
                or Event.PayloadOneofCase.ContextReminderInjected
                or Event.PayloadOneofCase.FinalProviderRequestPromptInjected
                or Event.PayloadOneofCase.ToolAvailabilityRestoredPromptInjected
                or Event.PayloadOneofCase.PlanValidationRepairInjected
                or Event.PayloadOneofCase.PendingChildQuestionReminderInjected
                when _hierarchy.IsChild(published.AgentSessionId):
                await commit(
                    Wrap(
                        GetNamedAgentSession(published.AgentSessionId),
                        new ActivityNoticeScrollbackValue(TerminalIcons.StatusNotice, DescribeNotice(published))),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.ToolRequestReceived when published.ToolRequestReceived.ToolCallCount > 1:
                await commit(
                    Wrap(
                        GetNamedAgentSession(published.AgentSessionId),
                        new ActivityNoticeScrollbackValue(
                            TerminalIcons.ToolRequest,
                            $"requested {published.ToolRequestReceived.ToolCallCount} tool calls")),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
                break;

            case Event.PayloadOneofCase.ReasoningChunk:
            {
                var chunk = published.ReasoningChunk;
                var state = GetNamedAgentSession(published.AgentSessionId);
                if (state.HasReasoning && state.IsReasoningSummary != (chunk.Kind == ReasoningKind.Summary))
                {
                    await CommitReasoning(state, cancellationToken).ConfigureAwait(false);
                }

                state.CollectReasoning(chunk);
                if (chunk.Completed)
                {
                    await CommitReasoning(state, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                }

                break;
            }

            default:
                break;
        }
    }

    public async Task Shutdown()
    {
        Task shutdown;
        lock (_shutdownLock)
        {
            shutdown = _shutdownTask ??= ShutdownCore();
        }

        try
        {
            await shutdown.ConfigureAwait(false);
        }
        catch
        {
            _progressFailureReported = true;
            throw;
        }
    }

    internal static string ExitReminderNotice(ExitReminderChanged changed) =>
        changed.StateCase == ExitReminderChanged.StateOneofCase.Description
            ? $"Exit reminder set: {TerminalText.Sanitize(changed.Title)}: {TerminalText.Sanitize(changed.Description)}"
            : $"Exit reminder cleared: {TerminalText.Sanitize(changed.Title)}";

    private static bool ContainsHiddenTask(IEnumerable<AgentTaskProgressNode> nodes) =>
        nodes.Any(static node => node.Hidden || ContainsHiddenTask(node.Children));

    private static ILiveBufferItem EmbedTaskAgentLines(
        ILiveBufferItem value,
        IReadOnlyDictionary<string, TaskAgentLine> taskAgentLines,
        IReadOnlySet<string> activeAgentSessionIds,
        HashSet<string> embeddedAgentSessionIds)
    {
        if (value is not AgentTaskProgressLiveValue tree)
        {
            return value;
        }

        embeddedAgentSessionIds.UnionWith(
            AgentTaskProgressFormatter.GetEmbeddedAgentSessionIds(tree.Snapshot, taskAgentLines, activeAgentSessionIds));
        return new AgentTaskProgressLiveValue(tree.Snapshot, taskAgentLines, activeAgentSessionIds);
    }

    private static void CancelPending(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private static InventoryState ObserveInventory(Dictionary<string, InventoryState> inventories, string ownerAgentSessionId)
    {
        if (!inventories.TryGetValue(ownerAgentSessionId, out var inventory))
        {
            inventory = new InventoryState();
            inventories.Add(ownerAgentSessionId, inventory);
        }

        return inventory;
    }

    private static string DescribeNotice(Event published) => published.PayloadCase switch
    {
        Event.PayloadOneofCase.RetryNotice =>
            $"retry {published.RetryNotice.Attempt} in {published.RetryNotice.RetryAfterMs} ms: {published.RetryNotice.Reason}",
        Event.PayloadOneofCase.StatusInjected => "Status prompt injected",
        Event.PayloadOneofCase.ContextReminderInjected =>
            $"Context reminder injected ({published.ContextReminderInjected.UsagePercent}% context used)",
        Event.PayloadOneofCase.FinalProviderRequestPromptInjected => "Final provider request prompt injected",
        Event.PayloadOneofCase.ToolAvailabilityRestoredPromptInjected => "Tool availability restored prompt injected",
        Event.PayloadOneofCase.PlanValidationRepairInjected =>
            $"Retrying after plan validation failure: {published.PlanValidationRepairInjected.Diagnostic}",
        Event.PayloadOneofCase.PendingChildQuestionReminderInjected => "Retrying with pending child question reminder",
        _ => throw new InvalidOperationException($"The {published.PayloadCase} event is not an agent notice."),
    };

    private static string ProcessKey(string inventoryInstanceId, string processId) =>
        string.Concat(inventoryInstanceId, "\n", processId);

    private static (string OwnerAgentSessionId, string ToolCallId)? OriginTool(ActiveShellProcess process) =>
        process.OriginToolCallId.Length == 0
            ? null
            : (process.OwnerAgentSessionId, process.OriginToolCallId);

    private static (string Command, string Description) ReadProcessArguments(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            return (
                JsonRead.TryReadString(root, "command", out var command) ? command : JsonRead.String(root, "cmd"),
                JsonRead.String(root, "description"));
        }
        catch (JsonException)
        {
            return (string.Empty, string.Empty);
        }
    }

    private static HierarchicalScrollbackValue WrapProcess(ActiveShellProcess process, IScrollbackItem value) =>
        new(value, Math.Max(0, process.Depth), ProcessOwnerLabel(process));

    private static string? ProcessOwnerLabel(ActiveShellProcess process) => process.Depth == 0
        ? null
        : process.OwnerAgentName.Length == 0
            ? process.OwnerAgentSessionId
            : process.OwnerAgentName;

    private void ObserveProcessCompletions(string ownerAgentSessionId, IEnumerable<CompletedShellProcess> completions)
    {
        foreach (var completion in completions)
        {
            _processCompletions[(ownerAgentSessionId, completion.ProcessId)] = completion.Clone();
        }
    }

    private ProcessCompletionScrollbackValue ProcessCompletion(string command, string ownerAgentSessionId, string processId)
    {
        var elapsedMilliseconds = _processCompletions.TryGetValue((ownerAgentSessionId, processId), out var completion)
            && completion.HasElapsedMs
                ? completion.ElapsedMs
                : (long?)null;
        return new ProcessCompletionScrollbackValue(command, elapsedMilliseconds);
    }

    private async Task ShutdownCore()
    {
        using (await _rendering.Lock(CancellationToken.None).ConfigureAwait(false))
        {
            _progressShutdown = true;
            foreach (var pending in _pendingProgress.Values)
            {
                CancelPending(pending.Cancellation);
            }

            foreach (var pendingStart in _pendingStarts.Values)
            {
                CancelPending(pendingStart.Cancellation);
            }
        }

        Task[] tasks;
        lock (_progressTasksLock)
        {
            tasks = [.. _progressTasks];
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        ExceptionDispatchInfo? progressFailure = null;
        await _rendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            foreach (var pendingStart in _pendingStarts.Values)
            {
                try
                {
                    await commit(pendingStart.Scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    progressFailure ??= ExceptionDispatchInfo.Capture(failure);
                }
            }

            _pendingStarts.Clear();
            foreach (var entry in _pendingProgress.OrderBy(static entry => entry.Key, StringComparer.Ordinal).ToArray())
            {
                try
                {
                    await FlushProgress(entry.Key, entry.Value, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    progressFailure ??= ExceptionDispatchInfo.Capture(failure);
                }
            }

            _pendingProgress.Clear();
            progressFailure ??= _progressFailure;
        }
        finally
        {
            _ = _rendering.Release();
            DisposeSynchronization();
        }

        progressFailure?.Throw();
    }

    private void ScheduleProgress(AgentSessionState state, AgentTaskProgressSnapshot snapshot)
    {
        var key = state.AgentSessionId;
        var flushedRevision = 0UL;
        if (_pendingProgress.TryGetValue(key, out var previous))
        {
            flushedRevision = previous.FlushedRevision;
            CancelPending(previous.Cancellation);
        }

        var pending = new PendingProgress(snapshot.Clone(), flushedRevision);
        _pendingProgress[key] = pending;
        TrackProgressTask(DelayAndRun(
            pending.Cancellation,
            ProgressQuietPeriod,
            () => FlushProgress(key, pending, pending.Cancellation.Token)));
    }

    private async Task DelayAndRun(CancellationTokenSource cancellation, TimeSpan quietPeriod, Func<Task> run)
    {
        try
        {
            await progressDelay(quietPeriod, cancellation.Token).ConfigureAwait(false);
            using var renderingLock = await _rendering.Lock(cancellation.Token).ConfigureAwait(false);
            await run().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception failure)
        {
            using var renderingLock = await _rendering.Lock(CancellationToken.None).ConfigureAwait(false);
            _progressFailure ??= ExceptionDispatchInfo.Capture(failure);
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private async Task FlushProgress(
        string key,
        PendingProgress pending,
        CancellationToken cancellationToken)
    {
        if (!_pendingProgress.TryGetValue(key, out var current)
            || !ReferenceEquals(current, pending)
            || pending.FlushedRevision >= pending.Snapshot.Revision
            || !_agentSessions.TryGetValue(key, out var state)
            || !state.IsCurrentAgentTaskProgress(pending.Snapshot.Revision))
        {
            return;
        }

        if (AgentTaskProgressFormatter.Format(pending.Snapshot).Count > 0)
        {
            await commit(
                Wrap(state, new AgentTaskProgressScrollbackValue(pending.Snapshot)),
                Snapshot(),
                cancellationToken).ConfigureAwait(false);
        }

        pending.FlushedRevision = pending.Snapshot.Revision;
        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    private void TrackProgressTask(Task task)
    {
        lock (_progressTasksLock)
        {
            _ = _progressTasks.Add(task);
        }

        _ = task.ContinueWith(
            completed =>
            {
                lock (_progressTasksLock)
                {
                    _ = _progressTasks.Remove(completed);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void DisposeSynchronization()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _rendering.Dispose();
    }

    private void ObserveOwnerHierarchy(
        string ownerAgentSessionId,
        string ownerAgentName,
        string parentAgentSessionId,
        string parentAgentName)
    {
        _hierarchy.ObserveOwner(ownerAgentSessionId, ownerAgentName, parentAgentSessionId, parentAgentName);
        NameAgentSession(ownerAgentSessionId, ownerAgentName);
        NameAgentSession(parentAgentSessionId, parentAgentName);
    }

    private void NameAgentSession(string agentSessionId, string name)
    {
        if (agentSessionId.Length == 0)
        {
            return;
        }

        var state = GetAgentSession(agentSessionId);
        if (!state.HasName && name.Length > 0)
        {
            state.UpdateName(name);
        }
    }

    private List<ILiveBufferItem> Snapshot()
    {
        var items = new List<ILiveBufferItem>(_content.Count + _activities.Count + 1);
        items.AddRange(_content.Select(item => item.AnimateSinceCapture(_frame)));
        if (_hierarchy.RootSessionId is { } rootSessionId
            && _agentSessions.TryGetValue(rootSessionId, out var rootState)
            && rootState.HasReasoning)
        {
            items.Add(rootState.CreateReasoningItem(_frame));
        }

        // Keep main-agent activity on the modeline, never in the live buffer.
        var activities = _activities
            .Where(activity =>
                (!_hierarchy.IsRoot(activity.State.AgentSessionId)
                 || !activity.State.IsAgentActivity(activity.ActivityId))
                && !activity.State.IsFoldedActivity(activity.ActivityId))
            .ToList();
        var processes = _processes.Values
            .Where(process => !IsOriginToolActive(process.Process))
            .ToList();
        var activeAgentSessionIds = _agentSessions.Values
            .Where(static state => state.IsAgentActive)
            .Select(static state => state.AgentSessionId)
            .ToHashSet(StringComparer.Ordinal);
        var taskTrees = _agentSessions.Values
            .SelectMany(static state => state.AgentTaskProgress is { } tree ? [(state.AgentSessionId, Tree: tree)] : Array.Empty<(string AgentSessionId, AgentTaskProgressSnapshot Tree)>())
            .ToDictionary(static entry => entry.AgentSessionId, static entry => entry.Tree, StringComparer.Ordinal);
        foreach (var key in taskTrees.Keys.ToArray())
        {
            if (!_pendingProgress.TryGetValue(key, out var pending)
                || pending.FlushedRevision < pending.Snapshot.Revision
                || ContainsHiddenTask(AgentTaskProgressFormatter.Nest(taskTrees[key], taskTrees).RootNodes)
                || _agentSessions.Values.Any(agent => agent.IsAgentActive && _hierarchy.IsDescendant(agent.AgentSessionId, key))
                || !_agentSessions[key].RetireAgentTaskProgress(pending.Snapshot.Revision))
            {
                continue;
            }

            _ = _pendingProgress.Remove(key);
            _ = taskTrees.Remove(key);
        }

        var nestedTaskTrees = taskTrees.Values
            .SelectMany(static tree => tree.RootNodes)
            .Select(static node => node.AgentSessionId)
            .Where(taskTrees.ContainsKey)
            .ToHashSet(StringComparer.Ordinal);
        var visibleTaskTrees = taskTrees
            .Where(tree => !nestedTaskTrees.Contains(tree.Key))
            .Select(tree => (tree.Key, Tree: AgentTaskProgressFormatter.Nest(tree.Value, taskTrees)))
            .Where(tree => AgentTaskProgressFormatter.FormatRows(tree.Tree, null, activeAgentSessionIds).Count > 0)
            .ToArray();
        var ownerIds = activities.Select(static activity => activity.State.AgentSessionId)
            .Concat(processes.Select(static process => process.Process.OwnerAgentSessionId))
            .Concat(_queues.Keys.Select(static key => key.OwnerAgentSessionId))
            .Concat(_agentSessions.Values.Where(state => state.HasReasoning && _hierarchy.IsChild(state.AgentSessionId))
                .Select(static state => state.AgentSessionId))
            .Concat(visibleTaskTrees.Select(static tree => tree.Key))
            .Concat(visibleTaskTrees
                .SelectMany(tree => AgentTaskProgressFormatter.GetVisibleChildGraphOwnerSessionIds(tree.Tree, activeAgentSessionIds))
                .Where(taskTrees.ContainsKey))
            .Where(static ownerId => ownerId.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var state in _agentSessions.Values)
        {
            if (!_hierarchy.IsRoot(state.AgentSessionId)
                && !state.IsAgentActive
                && ownerIds.Any(ownerId => string.Equals(ownerId, state.AgentSessionId, StringComparison.Ordinal)
                    || _hierarchy.IsDescendant(ownerId, state.AgentSessionId)))
            {
                _ = ownerIds.Add(state.AgentSessionId);
            }
        }

        var order = _hierarchy.GetPostOrder(ownerIds);
        var taskAgentLines = activities
            .Where(activity => _hierarchy.IsChild(activity.State.AgentSessionId)
                && activity.State.IsAgentActivity(activity.ActivityId))
            .ToDictionary(
                static activity => activity.State.AgentSessionId,
                activity => CreateTaskAgentLine(activity.State),
                StringComparer.Ordinal);
        var embeddedAgentSessionIds = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<(string OwnerId, int Kind, string Id, ILiveBufferItem Item)>();
        rows.AddRange(visibleTaskTrees
            .Select(tree =>
            (
                tree.Key,
                0,
                "task",
                (ILiveBufferItem)new HierarchicalLiveValue(
                    EmbedTaskAgentLines(
                        new AgentTaskProgressLiveValue(tree.Tree, null, activeAgentSessionIds),
                        taskAgentLines,
                        activeAgentSessionIds,
                        embeddedAgentSessionIds),
                    _hierarchy.GetDepth(tree.Key),
                    _hierarchy.GetLabel(tree.Key),
                    null))));
        rows.AddRange(_agentSessions.Values
            .Where(state => state.HasReasoning && _hierarchy.IsChild(state.AgentSessionId))
            .Select(state => (
                state.AgentSessionId,
                0,
                "reasoning",
                (ILiveBufferItem)new HierarchicalLiveValue(
                    state.CreateReasoningItem(_frame),
                    _hierarchy.GetDepth(state.AgentSessionId),
                    _hierarchy.GetLabel(state.AgentSessionId),
                    null))));

        // Task trees are built first so an agent shown inside one keeps only its streamed response in its own branch.
        rows.AddRange(activities
            .Select(activity => (Activity: activity, Item: CreateActivityItem(activity, taskAgentLines, activeAgentSessionIds, embeddedAgentSessionIds)))
            .ToList()
            .Where(row => !row.Activity.State.IsAgentActivity(row.Activity.ActivityId)
                || row.Activity.State.IsStreamingResponse
                || !embeddedAgentSessionIds.Contains(row.Activity.State.AgentSessionId))
            .Select(static row => (
                row.Activity.State.AgentSessionId,
                row.Activity.State.IsAgentActivity(row.Activity.ActivityId) ? 3 : 0,
                row.Activity.ActivityId,
                (ILiveBufferItem)row.Item)));
        rows.AddRange(processes.Select(process => (
            process.Process.OwnerAgentSessionId,
            1,
            process.Process.ProcessId,
            (ILiveBufferItem)CreateProcessItem(process))));
        rows.AddRange(_queues.Select(queue => (
            queue.Key.OwnerAgentSessionId,
            2,
            queue.Key.Name,
            CreateQueueItem(queue.Key.OwnerAgentSessionId, queue.Value))));
        rows.AddRange((IEnumerable<(string OwnerId, int Kind, string Id, ILiveBufferItem Item)>)_agentSessions.Values
            .Where(state => !_hierarchy.IsRoot(state.AgentSessionId)
                && !state.IsAgentActive
                && ownerIds.Contains(state.AgentSessionId)
                && !activities.Any(activity => ReferenceEquals(activity.State, state)
                    && state.IsAgentActivity(activity.ActivityId)))
            .Select(state => (
                state.AgentSessionId,
                3,
                "agent",
                (ILiveBufferItem)CreateNeutralAgentItem(state))));
        items.AddRange(rows
            .OrderBy(row => row.OwnerId.Length == 0 ? int.MinValue : order[row.OwnerId])
            .ThenBy(static row => row.Kind)
            .ThenBy(static row => row.Id, StringComparer.Ordinal)
            .Select(static row => row.Item));
        return items;
    }

    private List<ILiveBufferItem> SnapshotWithout(AgentSessionState state, string activityId)
    {
        var removed = _activities.Remove((state, activityId));
        try
        {
            return Snapshot();
        }
        finally
        {
            if (removed)
            {
                _activities.Add((state, activityId));
            }
        }
    }

    private IReadOnlyList<ILiveBufferItem> Capture(IReadOnlyList<ILiveBufferItem> items) =>
        [.. items.Select(item => item.CaptureAnimation(_frame))];

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

    private async Task StartTurn(
        string agentSessionId,
        LiveModelAliasIcon? modelAliasIcon,
        CancellationToken cancellationToken)
    {
        var state = GetNamedAgentSession(agentSessionId);
        if (state.StartTurn(modelAliasIcon) is { } activityId)
        {
            _activities.Add((state, activityId));
            if (_hierarchy.IsRoot(agentSessionId))
            {
                await updateMainAgentActivity(state.ModelineLabel, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private void StartCompaction(string agentSessionId)
    {
        var state = GetNamedAgentSession(agentSessionId);
        if (state.StartCompaction() is { } activityId)
        {
            _activities.Add((state, activityId));
        }
    }

    private async Task FinishCompaction(Event published, CancellationToken cancellationToken)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        if (state.FinishCompaction(published) is not { } completion)
        {
            return;
        }

        _ = _activities.Remove((state, completion.ActivityId));
        await commit(
            Wrap(state, completion.Notice),
            Snapshot(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishTurn(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (_hierarchy.IsChild(published.AgentSessionId))
        {
            if (await state.FinishChildTurn(response => commit(
                    Wrap(state, new FinalMessageScrollbackValue(response)),
                    SnapshotWithout(state, AgentSessionState.AgentActivityId),
                    cancellationToken)).ConfigureAwait(false))
            {
                _ = _activities.Remove((state, AgentSessionState.AgentActivityId));
                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
            }

            return;
        }

        if (state.FinishTurn(published, failed) is not { } completion)
        {
            return;
        }

        if (_hierarchy.IsRoot(published.AgentSessionId))
        {
            await updateMainAgentActivity(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await CommitCompletion(state, completion.Response, completion.Notice, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAgent(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (state.FinishAgent(published, failed) is not { } notice)
        {
            return;
        }

        if (_hierarchy.IsRoot(published.AgentSessionId))
        {
            await updateMainAgentActivity(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await CommitCompletion(state, string.Empty, notice, cancellationToken).ConfigureAwait(false);
        state.CompleteAgent();
    }

    private async Task UpdateAgentName(string agentSessionId, string name, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(agentSessionId);
        state.UpdateName(name);
        await UpdateMainAgentActivity(agentSessionId, state, cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateAgentStatistics(
        string agentSessionId,
        AgentStatisticsUpdatedEvent statistics,
        CancellationToken cancellationToken)
    {
        var state = GetAgentSession(agentSessionId);
        state.UpdateStatistics(statistics);
        await UpdateMainAgentActivity(agentSessionId, state, cancellationToken).ConfigureAwait(false);
    }

    private Task UpdateMainAgentActivity(
        string agentSessionId,
        AgentSessionState state,
        CancellationToken cancellationToken) =>
        _hierarchy.IsRoot(agentSessionId) && _activities.Any(activity =>
            ReferenceEquals(activity.State, state) && state.IsAgentActivity(activity.ActivityId))
            ? updateMainAgentActivity(state.ModelineLabel, cancellationToken)
            : Task.CompletedTask;

    private void ToolCall(string agentSessionId, ToolCallChunk chunk) =>
        GetAgentSession(agentSessionId).CollectToolCall(chunk);

    private void RefreshToolPresentations()
    {
        foreach (var state in _agentSessions.Values)
        {
            state.RefreshToolPresentations();
        }
    }

    private async Task StartTool(Event published, CancellationToken cancellationToken)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        var metadata = presenters.Describe(published.ToolStarted.ToolName);
        var isRoot = _hierarchy.IsRoot(published.AgentSessionId);
        var foldIntoAgentStatus = metadata.Modeline
            && !metadata.TerminalOnly
            && !isRoot;
        var activityId = state.StartTool(published.ToolStarted, foldIntoAgentStatus);
        if (activityId is not null
            && !metadata.TerminalOnly
            && !(metadata.Modeline && isRoot))
        {
            _activities.Add((state, activityId));
        }

        var started = activityId is not null
            ? state.PresentStartedTool(
                published.ToolStarted.ToolCallId,
                presenters,
                reference => _hierarchy.ResolveAgentReference(state.AgentSessionId, reference),
                isRoot)
            : null;
        if (started is null)
        {
            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        else if (metadata.DeferStarted && !_progressShutdown)
        {
            var key = (published.AgentSessionId, published.ToolStarted.ToolCallId);
            var pendingStart = new PendingStart(Wrap(state, started));
            _pendingStarts[key] = pendingStart;
            TrackProgressTask(DelayAndRun(
                pendingStart.Cancellation,
                StartGracePeriod,
                () => _pendingStarts.Remove(key)
                    ? commit(pendingStart.Scrollback, Snapshot(), pendingStart.Cancellation.Token)
                    : Task.CompletedTask));
            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await commit(Wrap(state, started), Snapshot(), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task FinishTool(Event published, CancellationToken cancellationToken)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        var toolCallId = TerminalToolEvent.ReadTool(published).ToolCallId;
        var key = (published.AgentSessionId, toolCallId);
        var startOmitted = false;
        if (_pendingStarts.Remove(key, out var pendingStart))
        {
            CancelPending(pendingStart.Cancellation);
            if (published.PayloadCase == Event.PayloadOneofCase.ToolFinished && published.ToolFinished.YieldedProcess is not null)
            {
                await commit(pendingStart.Scrollback, Snapshot(), cancellationToken).ConfigureAwait(false);
            }
            else
            {
                startOmitted = true;
            }
        }

        var (activityId, scrollback, call, terminal) = state.FinishTool(
            published,
            presenters,
            reference => _hierarchy.ResolveAgentReference(state.AgentSessionId, reference),
            startOmitted);
        _ = _activities.Remove((state, activityId));
        var deferred = terminal.YieldedProcess;
        if (deferred is null && string.Equals(call.ToolName, "exec_command", StringComparison.Ordinal))
        {
            var origin = (published.AgentSessionId, toolCallId);
            if (!_omittedProcessTools.Remove(origin)
                && _processes.Values.Any(process => OriginTool(process.Process) == origin))
            {
                _ = _terminalProcessTools.Add(origin);
            }
        }

        if (deferred is not null)
        {
            var (command, description) = ReadProcessArguments(call.ArgumentsJson);
            scrollback = ObserveDeferredProcess(deferred, published, state.Name, command, description)
                && !string.IsNullOrWhiteSpace(command)
                && _completedProcesses.Add((published.AgentSessionId, ProcessKey(deferred.InventoryInstanceId, deferred.ProcessId)))
                    ? ProcessCompletion(
                        ExecCommandToolPresenter.Summarize(deferred.Name, description, command),
                        published.AgentSessionId,
                        deferred.ProcessId)
                    : null;
        }

        if (scrollback is null)
        {
            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await commit(Wrap(state, scrollback), Snapshot(), cancellationToken).ConfigureAwait(false);
        }
    }

    private TaskAgentLine CreateTaskAgentLine(AgentSessionState state)
    {
        var spinner = TerminalIcons.SpinnerFrames[_frame % TerminalIcons.SpinnerFrames.Length];
        var glyph = state.ModelAliasIcon is { } icon ? $"{icon.Glyph} " : string.Empty;
        var lead = TerminalText.Sanitize($"{spinner} [{_hierarchy.GetLabel(state.AgentSessionId)}] ")
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        var text = lead + TerminalText.Sanitize($"{glyph}{state.AgentSpinnerText}")
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        return new TaskAgentLine(text, state.ModelAliasIcon) { GlyphStartIndex = lead.Length };
    }

    private HierarchicalLiveValue CreateActivityItem(
        (AgentSessionState State, string ActivityId) activity,
        IReadOnlyDictionary<string, TaskAgentLine> taskAgentLines,
        IReadOnlySet<string> activeAgentSessionIds,
        HashSet<string> embeddedAgentSessionIds)
    {
        var value = EmbedTaskAgentLines(
            activity.State.CreateLiveBufferItem(
                activity.ActivityId,
                _frame,
                presenters,
                reference => _hierarchy.ResolveAgentReference(activity.State.AgentSessionId, reference)),
            taskAgentLines,
            activeAgentSessionIds,
            embeddedAgentSessionIds);
        var isAgentActivity = activity.State.IsAgentActivity(activity.ActivityId);
        var depth = _hierarchy.GetDepth(activity.State.AgentSessionId);
        var modelAliasIcon = _hierarchy.IsChild(activity.State.AgentSessionId) && isAgentActivity
            ? activity.State.ModelAliasIcon
            : null;
        return new HierarchicalLiveValue(
            value,
            depth,
            _hierarchy.GetLabel(activity.State.AgentSessionId),
            modelAliasIcon);
    }

    private bool IsOriginToolActive(ActiveShellProcess process) =>
        process.OriginToolCallId.Length > 0
        && _agentSessions.TryGetValue(process.OwnerAgentSessionId, out var owner)
        && owner.IsToolActive(process.OriginToolCallId);

    private ILiveBufferItem CreateQueueItem(string ownerAgentSessionId, ILiveBufferItem queue)
    {
        if (ownerAgentSessionId.Length == 0 || _hierarchy.IsRoot(ownerAgentSessionId))
        {
            return queue;
        }

        return new HierarchicalLiveValue(
            queue,
            _hierarchy.GetDepth(ownerAgentSessionId),
            _hierarchy.GetLabel(ownerAgentSessionId) ?? ownerAgentSessionId,
            null);
    }

    private HierarchicalLiveValue CreateNeutralAgentItem(AgentSessionState state) =>
        new(
            new NeutralAgentLiveBufferItem(state.Name),
            Math.Max(0, _hierarchy.GetDepth(state.AgentSessionId) - 1),
            _hierarchy.GetLabel(state.AgentSessionId),
            null);

    private HierarchicalLiveValue CreateProcessItem(ProcessState process) =>
        new(
            new ShellProcessLiveValue(process.Process, process.ObservedTimestamp, _timeProvider, _frame),
            Math.Max(0, process.Process.Depth),
            ProcessOwnerLabel(process.Process),
            null);

    private HierarchicalScrollbackValue Wrap(AgentSessionState state, IScrollbackItem value) =>
        new(value, _hierarchy.GetDepth(state.AgentSessionId), _hierarchy.GetLabel(state.AgentSessionId));

    private Task CommitReasoning(AgentSessionState state, CancellationToken cancellationToken) =>
        state.FlushReasoning(async reasoning =>
        {
            if (state.IsReasoningSummary && !_hierarchy.IsRoot(state.AgentSessionId))
            {
                await CommitResponse(state, cancellationToken).ConfigureAwait(false);
            }

            await commit(Wrap(state, reasoning), Snapshot(), cancellationToken).ConfigureAwait(false);
        });

    private Task CommitResponse(AgentSessionState state, CancellationToken cancellationToken) =>
        state.FlushResponse(response => commit(
            Wrap(state, new FinalMessageScrollbackValue(response)),
            Snapshot(),
            cancellationToken));

    private async Task CommitCompletion(
        AgentSessionState state,
        string response,
        ActivityNoticeScrollbackValue notice,
        CancellationToken cancellationToken)
    {
        _ = _activities.Remove((state, AgentSessionState.AgentActivityId));
        if (response.Length > 0)
        {
            await commit(
                Wrap(state, new FinalMessageScrollbackValue(response)),
                Snapshot(),
                cancellationToken).ConfigureAwait(false);
        }

        await commit(Wrap(state, notice), Snapshot(), cancellationToken).ConfigureAwait(false);
    }

    private bool ObserveDeferredProcess(
        YieldedShellProcess yielded,
        Event published,
        string ownerAgentName,
        string command,
        string description)
    {
        var inventory = ObserveInventory(_processInventories, published.AgentSessionId);
        if (inventory.RetiredInstances.Contains(yielded.InventoryInstanceId))
        {
            return false;
        }

        if (_completedProcesses.Contains((published.AgentSessionId, ProcessKey(yielded.InventoryInstanceId, yielded.ProcessId))))
        {
            return true;
        }

        if (string.Equals(inventory.InstanceId, yielded.InventoryInstanceId, StringComparison.Ordinal))
        {
            if (_processes.TryGetValue(yielded.ProcessId, out var observed))
            {
                _processes[yielded.ProcessId] = observed.Defer(command, yielded.VisibleRevision);
                return false;
            }

            if (_processCompletions.ContainsKey((published.AgentSessionId, yielded.ProcessId))
                || inventory.Revision > yielded.VisibleRevision)
            {
                return true;
            }
        }

        if (inventory.InstanceId is not null
            && !string.Equals(inventory.InstanceId, yielded.InventoryInstanceId, StringComparison.Ordinal))
        {
            _ = inventory.RetiredInstances.Add(inventory.InstanceId);
            ClearOwnerProcesses(published.AgentSessionId);
            inventory.Revision = 0;
        }

        inventory.InstanceId = yielded.InventoryInstanceId;
        var process = new ActiveShellProcess
        {
            ProcessId = yielded.ProcessId,
            Name = yielded.Name,
            Command = command,
            Description = description,
            OriginToolCallId = published.ToolFinished.ToolCallId,
            OwnerAgentSessionId = published.AgentSessionId,
            OwnerAgentName = ownerAgentName,
            ParentAgentSessionId = string.Empty,
            ParentAgentName = string.Empty,
            Depth = _hierarchy.GetDepth(published.AgentSessionId),
            ElapsedMs = 0,
        };
        var state = new ProcessState(
            process,
            yielded.InventoryInstanceId,
            yielded.VisibleRevision,
            _timeProvider.GetTimestamp(),
            true,
            command);
        _processes[yielded.ProcessId] = state;
        return false;
    }

    private void ClearOwnerProcesses(string ownerAgentSessionId)
    {
        foreach (var processId in _processes.Where(process => string.Equals(
            process.Value.Process.OwnerAgentSessionId, ownerAgentSessionId, StringComparison.Ordinal))
            .Select(static process => process.Key).ToArray())
        {
            _ = _processes.Remove(processId);
        }

        foreach (var key in _processCompletions.Keys.Where(key => string.Equals(
            key.OwnerAgentSessionId, ownerAgentSessionId, StringComparison.Ordinal)).ToArray())
        {
            _ = _processCompletions.Remove(key);
        }

        _ = _completedProcesses.RemoveWhere(process => string.Equals(process.OwnerAgentSessionId, ownerAgentSessionId, StringComparison.Ordinal));
        _ = _omittedProcessTools.RemoveWhere(tool => string.Equals(tool.OwnerAgentSessionId, ownerAgentSessionId, StringComparison.Ordinal));
        _ = _terminalProcessTools.RemoveWhere(tool => string.Equals(tool.OwnerAgentSessionId, ownerAgentSessionId, StringComparison.Ordinal));
    }

    private readonly record struct NeutralAgentLiveBufferItem(string Name) : ILiveBufferItem
    {
        public MultiLine Render(LiveBufferRenderContext context) => new(
            [.. context.Decoration.Apply(
                    TerminalIcons.Agent,
                    [TerminalText.Clip($"agent {TerminalText.Sanitize(Name)}", context.Decoration.ContentColumns(context.Columns))])
                .Select(line => new TerminalLine(line, context.Palette.LiveMuted))],
            null,
            LiveBufferRetention.Fixed);
    }

    private sealed class InventoryState
    {
        public string? InstanceId { get; set; }

        public ulong Revision { get; set; }

        public HashSet<string> RetiredInstances { get; } = new(StringComparer.Ordinal);

        public bool Accept(string instanceId, ulong revision, bool removed, out bool replaced)
        {
            replaced = InstanceId is not null && !string.Equals(InstanceId, instanceId, StringComparison.Ordinal);
            if (RetiredInstances.Contains(instanceId)
                || (!replaced && InstanceId is not null && revision <= Revision))
            {
                return false;
            }

            if (replaced && InstanceId is { } previousInstanceId)
            {
                _ = RetiredInstances.Add(previousInstanceId);
            }

            InstanceId = instanceId;
            Revision = revision;
            if (removed)
            {
                _ = RetiredInstances.Add(instanceId);
            }

            return true;
        }
    }

    private sealed record ProcessState(
        ActiveShellProcess Process,
        string InventoryInstanceId,
        ulong Revision,
        long ObservedTimestamp,
        bool IsDeferred,
        string Command)
    {
        public static ProcessState Observe(
            ActiveShellProcess process,
            string inventoryInstanceId,
            ulong revision,
            long observedTimestamp) =>
            new(
                process,
                inventoryInstanceId,
                revision,
                observedTimestamp,
                false,
                process.Command);

        public ProcessState Observe(ActiveShellProcess process, ulong revision, long observedTimestamp)
        {
            var command = Command.Length == 0 ? process.Command : Command;
            if (IsDeferred && command.Length > 0)
            {
                process = process.Clone();
                process.Command = command;
            }

            return this with
            {
                Process = process,
                Revision = revision,
                ObservedTimestamp = observedTimestamp,
                Command = command,
            };
        }

        public ProcessState Defer(string command, ulong revision)
        {
            var process = Process.Clone();
            var originalCommand = process.Command.Length == 0 ? command : process.Command;
            process.Command = originalCommand;
            return this with
            {
                Process = process,
                Revision = Math.Max(Revision, revision),
                IsDeferred = true,
                Command = originalCommand,
            };
        }
    }

    private sealed class PendingStart(IScrollbackItem scrollback)
    {
        public IScrollbackItem Scrollback { get; } = scrollback;

        public CancellationTokenSource Cancellation { get; } = new();
    }

    private sealed class PendingProgress(
        AgentTaskProgressSnapshot snapshot,
        ulong flushedRevision)
    {
        public AgentTaskProgressSnapshot Snapshot { get; } = snapshot;

        public CancellationTokenSource Cancellation { get; } = new();

        public ulong FlushedRevision { get; set; } = flushedRevision;
    }
}

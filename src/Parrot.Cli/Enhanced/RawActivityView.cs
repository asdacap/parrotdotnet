using System.Text;
using System.Text.Json;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class RawActivityView(
    Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
    Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
    Func<CancellationToken, Task> delay,
    ToolPresenterRegistry presenters,
    Func<string, CancellationToken, Task> updateMainAgentActivity) : IDisposable
{
    private const int SpinnerIntervalMilliseconds = 80;

    private readonly List<(AgentSessionState State, string ActivityId)> _activities = [];
    private readonly Dictionary<string, AgentSessionState> _agentSessions = new(StringComparer.Ordinal);
    private readonly AgentSessionHierarchy _hierarchy = new();
    private readonly Dictionary<string, ProcessState> _processes = new(StringComparer.Ordinal);
    private readonly Dictionary<(string OwnerAgentSessionId, string Name), QueueLiveBufferItem> _queues = [];
    private readonly HashSet<string> _completedProcesses = new(StringComparer.Ordinal);
    private readonly HashSet<(string OwnerAgentSessionId, string ToolCallId)> _omittedProcessTools = [];
    private readonly HashSet<(string OwnerAgentSessionId, string ToolCallId)> _terminalProcessTools = [];
    private readonly HashSet<string> _retiredInventoryInstances = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    private readonly StringBuilder _reasoning = new();
    private readonly SemaphoreSlim _rendering = new(1, 1);

    private IReadOnlyList<ILiveBufferItem> _content = [];
    private string? _inventoryInstanceId;
    private int _frame;
    private ulong _inventoryRevision;

    public RawActivityView(
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> replace,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        ToolPresenterRegistry presenters,
        Func<string, CancellationToken, Task> updateMainAgentActivity)
        : this(
            replace,
            commit,
            static cancellationToken => Task.Delay(SpinnerIntervalMilliseconds, cancellationToken),
            presenters,
            updateMainAgentActivity)
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
                    if (_activities.Count > 0 || _processes.Count > 0)
                    {
                        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
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

    public async Task ReplaceContent(
        IReadOnlyList<ILiveBufferItem> items,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(items);

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _content = Capture(items);
            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
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
            _content = Capture(items);
            await commit(scrollback, Snapshot(), cancellationToken).ConfigureAwait(false);
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
            _ = _reasoning.Clear();
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    public async Task ReplaceProcesses(
        ShellProcessSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_retiredInventoryInstances.Contains(snapshot.InventoryInstanceId)
                || (string.Equals(_inventoryInstanceId, snapshot.InventoryInstanceId, StringComparison.Ordinal)
                    && snapshot.Revision <= _inventoryRevision))
            {
                return;
            }

            if (_inventoryInstanceId is not null
                && !string.Equals(_inventoryInstanceId, snapshot.InventoryInstanceId, StringComparison.Ordinal))
            {
                _ = _retiredInventoryInstances.Add(_inventoryInstanceId);
                _inventoryInstanceId = snapshot.InventoryInstanceId;
                _inventoryRevision = snapshot.Revision;
                _processes.Clear();
                _completedProcesses.Clear();
                _omittedProcessTools.Clear();
                _terminalProcessTools.Clear();
                foreach (var process in snapshot.Processes)
                {
                    ObserveProcessHierarchy(process);
                    _processes.Add(process.ProcessId, ProcessState.Observe(
                        process, snapshot.InventoryInstanceId, snapshot.Revision, _timeProvider.GetTimestamp()));
                }

                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                return;
            }

            var snapshotProcessIds = snapshot.Processes
                .Select(static process => process.ProcessId)
                .ToHashSet(StringComparer.Ordinal);
            var completions = _processes.Values
                .Where(process => process.Revision < snapshot.Revision
                    && !snapshotProcessIds.Contains(process.Process.ProcessId))
                .OrderBy(static process => process.Process.Depth)
                .ThenBy(static process => process.Process.OwnerAgentName, StringComparer.Ordinal)
                .ThenBy(static process => process.Process.Name, StringComparer.Ordinal)
                .ThenBy(static process => process.Process.ProcessId, StringComparer.Ordinal)
                .ToArray();
            var future = _processes.Values
                .Where(process => process.Revision >= snapshot.Revision
                    && !snapshotProcessIds.Contains(process.Process.ProcessId))
                .ToArray();
            var deferred = _processes.Values
                .Where(static process => process.IsDeferred)
                .ToDictionary(static process => process.Process.ProcessId, StringComparer.Ordinal);
            _inventoryInstanceId = snapshot.InventoryInstanceId;
            _inventoryRevision = snapshot.Revision;
            _processes.Clear();
            foreach (var process in snapshot.Processes)
            {
                ObserveProcessHierarchy(process);
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
                    && _completedProcesses.Add(ProcessKey(completion.InventoryInstanceId, completion.Process.ProcessId)))
                {
                    committed = true;
                    await commit(
                        WrapProcess(
                            completion.Process,
                            new ProcessCompletionScrollbackValue($"$ {completion.Command}")),
                        Snapshot(),
                        cancellationToken).ConfigureAwait(false);
                }
            }

            if (!committed)
            {
                await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    public async Task ReplaceQueues(
        string rootAgentSessionId,
        IReadOnlyList<QueueState> queues,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(queues);

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _hierarchy.ObserveRoot(rootAgentSessionId);
            RefreshToolPresentations();
            _queues.Clear();
            foreach (var queue in queues.Where(static queue => queue.Name.Length > 0 && queue.ItemCount > 0))
            {
                ObserveQueueHierarchy(queue);
                var key = (queue.OwnerAgentSessionId, queue.Name);
                _queues[key] = new QueueLiveBufferItem(queue.Name, queue.Description, queue.ItemCount)
                {
                    OwnerAgentSessionId = queue.OwnerAgentSessionId,
                };
            }

            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    public async Task Render(Event published, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(published);

        if (published.PayloadCase == Event.PayloadOneofCase.QueueSnapshot)
        {
            return;
        }

        await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
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
                case Event.PayloadOneofCase.ToolCallChunk:
                    ToolCall(published.AgentSessionId, published.ToolCallChunk);
                    break;
                case Event.PayloadOneofCase.TextChunk when _hierarchy.IsChild(published.AgentSessionId):
                    GetNamedAgentSession(published.AgentSessionId).CollectResponse(published.TextChunk.Fragment);
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ToolStarted:
                    StartTool(published);
                    await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.ToolFinished:
                case Event.PayloadOneofCase.ToolCancelled:
                case Event.PayloadOneofCase.ToolError:
                    await FinishTool(published, cancellationToken).ConfigureAwait(false);
                    break;
                case Event.PayloadOneofCase.AgentTaskProgressSnapshot:
                    if (GetAgentSession(published.AgentSessionId)
                        .OfferAgentTaskProgress(published.AgentTaskProgressSnapshot))
                    {
                        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    }

                    break;
                case Event.PayloadOneofCase.ExitReminderInjected when _hierarchy.IsChild(published.AgentSessionId):
                    await commit(
                        Wrap(
                            GetNamedAgentSession(published.AgentSessionId),
                            ImmediateScrollbackValue.Muted(["↻ Exit reminder injected"]),
                            null),
                        Snapshot(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ReasoningChunk:
                {
                    var fragment = TerminalText.Sanitize(published.ReasoningChunk.Fragment);
                    if (published.ReasoningChunk.Kind == ReasoningKind.Summary)
                    {
                        var isRoot = _hierarchy.IsRoot(published.AgentSessionId);
                        if (isRoot)
                        {
                            _ = _reasoning.Clear();
                        }

                        if (fragment.Length > 0)
                        {
                            var summary = new ReasoningSummaryScrollbackValue(fragment);
                            await commit(
                                isRoot ? summary : Wrap(GetNamedAgentSession(published.AgentSessionId), summary, null),
                                Snapshot(),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else if (_hierarchy.IsRoot(published.AgentSessionId))
                    {
                        _ = _reasoning.Append(fragment);
                        await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
                    }

                    break;
                }

                default:
                    break;
            }
        }
        finally
        {
            _ = _rendering.Release();
        }
    }

    private static string ProcessKey(string inventoryInstanceId, string processId) =>
        string.Concat(inventoryInstanceId, "\n", processId);

    private static (string OwnerAgentSessionId, string ToolCallId)? OriginTool(ActiveShellProcess process) =>
        process.OriginToolCallId.Length == 0
            ? null
            : (process.OwnerAgentSessionId, process.OriginToolCallId);

    private static string ReadCommand(string argumentsJson)
    {
        try
        {
            using var document = JsonDocument.Parse(argumentsJson);
            return document.RootElement.TryGetProperty("command", out var command)
                && command.ValueKind == JsonValueKind.String
                    ? command.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static HierarchicalScrollbackValue WrapProcess(
        ActiveShellProcess process,
        IScrollbackItem value)
    {
        var owner = process.OwnerAgentName.Length == 0
            ? process.OwnerAgentSessionId
            : process.OwnerAgentName;
        return new HierarchicalScrollbackValue(
            value,
            Math.Max(0, process.Depth),
            process.Depth == 0 ? null : owner,
            owner,
            null);
    }

    private void ObserveQueueHierarchy(QueueState queue)
    {
        _hierarchy.Observe(queue);
        if (queue.OwnerAgentSessionId.Length > 0)
        {
            var state = GetAgentSession(queue.OwnerAgentSessionId);
            if (!state.HasName && queue.OwnerAgentName.Length > 0)
            {
                state.UpdateName(queue.OwnerAgentName);
            }
        }

        if (queue.ParentAgentSessionId.Length > 0)
        {
            var parent = GetAgentSession(queue.ParentAgentSessionId);
            if (!parent.HasName && queue.ParentAgentName.Length > 0)
            {
                parent.UpdateName(queue.ParentAgentName);
            }
        }

        RefreshToolPresentations();
    }

    private void ObserveProcessHierarchy(ActiveShellProcess process)
    {
        _hierarchy.Observe(process);
        if (process.OwnerAgentSessionId.Length > 0)
        {
            var state = GetAgentSession(process.OwnerAgentSessionId);
            if (!state.HasName && process.OwnerAgentName.Length > 0)
            {
                state.UpdateName(process.OwnerAgentName);
            }
        }

        if (process.ParentAgentSessionId.Length > 0)
        {
            var parent = GetAgentSession(process.ParentAgentSessionId);
            if (!parent.HasName && process.ParentAgentName.Length > 0)
            {
                parent.UpdateName(process.ParentAgentName);
            }
        }

        RefreshToolPresentations();
    }

    private List<ILiveBufferItem> Snapshot()
    {
        var items = new List<ILiveBufferItem>(_content.Count + _activities.Count + 1);
        items.AddRange(_content.Select(item => item is MarqueeValue value ? value.Animate(_frame - value.Frame) : item));
        if (_reasoning.Length > 0)
        {
            items.Add(new SpinnerValue("Thinking…", _frame));
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
        var ownerIds = activities.Select(static activity => activity.State.AgentSessionId)
            .Concat(processes.Select(static process => process.Process.OwnerAgentSessionId))
            .Concat(_queues.Keys.Select(static key => key.OwnerAgentSessionId))
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
        var rows = new List<(string OwnerId, int Kind, string Id, ILiveBufferItem Item)>();
        rows.AddRange(activities.Select(activity => (
            activity.State.AgentSessionId,
            activity.State.IsAgentActivity(activity.ActivityId) ? 3 : 0,
            activity.ActivityId,
            (ILiveBufferItem)CreateActivityItem(activity))));
        rows.AddRange(processes.Select(process => (
            process.Process.OwnerAgentSessionId,
            1,
            process.Process.ProcessId,
            (ILiveBufferItem)CreateProcessItem(process))));
        rows.AddRange(_queues.Values.Select(queue => (
            queue.OwnerAgentSessionId,
            2,
            queue.Name,
            CreateQueueItem(queue))));
        rows.AddRange((IEnumerable<(string OwnerId, int Kind, string Id, ILiveBufferItem Item)>)_agentSessions.Values
            .Where(state => !_hierarchy.IsRoot(state.AgentSessionId)
                && !state.IsAgentActive
                && ownerIds.Any(ownerId => string.Equals(ownerId, state.AgentSessionId, StringComparison.Ordinal)
                    || _hierarchy.IsDescendant(ownerId, state.AgentSessionId))
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

    private IReadOnlyList<ILiveBufferItem> Capture(IReadOnlyList<ILiveBufferItem> items) =>
        [.. items.Select(item => item is MarqueeValue value ? value.Animate(_frame) : item)];

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
            Wrap(state, ImmediateScrollbackValue.Muted([completion.Line]), "✓"),
            Snapshot(),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishTurn(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (state.FinishTurn(published, failed) is not { } completion)
        {
            return;
        }

        if (_hierarchy.IsRoot(published.AgentSessionId))
        {
            await updateMainAgentActivity(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await CommitCompletion(state, completion, cancellationToken).ConfigureAwait(false);
    }

    private async Task FinishAgent(Event published, bool failed, CancellationToken cancellationToken)
    {
        var state = GetAgentSession(published.AgentSessionId);
        if (state.FinishAgent(published, failed) is not { } completion)
        {
            return;
        }

        if (_hierarchy.IsRoot(published.AgentSessionId))
        {
            await updateMainAgentActivity(string.Empty, cancellationToken).ConfigureAwait(false);
        }

        await CommitCompletion(state, completion, cancellationToken).ConfigureAwait(false);
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

    private void StartTool(Event published)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        var metadata = presenters.Describe(published.ToolStarted.ToolName);
        var foldIntoAgentStatus = metadata.Modeline
            && !metadata.TerminalOnly
            && !_hierarchy.IsRoot(published.AgentSessionId);
        if (state.StartTool(published.ToolStarted, foldIntoAgentStatus) is { } activityId
            && !metadata.TerminalOnly
            && !(metadata.Modeline && _hierarchy.IsRoot(published.AgentSessionId)))
        {
            _activities.Add((state, activityId));
        }
    }

    private async Task FinishTool(Event published, CancellationToken cancellationToken)
    {
        var state = GetNamedAgentSession(published.AgentSessionId);
        var (activityId, scrollback, call, terminal) = state.FinishTool(
            published,
            presenters,
            reference => _hierarchy.ResolveAgentReference(state.AgentSessionId, reference));
        _ = _activities.Remove((state, activityId));
        var deferred = terminal.YieldedProcess;
        if (deferred is null && string.Equals(call.ToolName, "exec_command", StringComparison.Ordinal))
        {
            var toolCallId = published.PayloadCase switch
            {
                Event.PayloadOneofCase.ToolFinished => published.ToolFinished.ToolCallId,
                Event.PayloadOneofCase.ToolCancelled => published.ToolCancelled.ToolCallId,
                _ => published.ToolError.ToolCallId,
            };
            var origin = (published.AgentSessionId, toolCallId);
            if (!_omittedProcessTools.Remove(origin)
                && _processes.Values.Any(process => OriginTool(process.Process) == origin))
            {
                _ = _terminalProcessTools.Add(origin);
            }
        }

        if (deferred is not null)
        {
            var command = ReadCommand(call.ArgumentsJson);
            scrollback = ObserveDeferredProcess(deferred, published, call, command)
                && !string.IsNullOrWhiteSpace(command)
                && _completedProcesses.Add(ProcessKey(deferred.InventoryInstanceId, deferred.ProcessId))
                    ? new ProcessCompletionScrollbackValue($"$ {command}")
                    : null;
        }

        if (scrollback is null)
        {
            await replace(Snapshot(), cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await commit(Wrap(state, scrollback, null), Snapshot(), cancellationToken).ConfigureAwait(false);
        }
    }

    private HierarchicalLiveValue CreateActivityItem((AgentSessionState State, string ActivityId) activity)
    {
        var value = activity.State.CreateLiveBufferItem(
            activity.ActivityId,
            _frame,
            presenters,
            reference => _hierarchy.ResolveAgentReference(activity.State.AgentSessionId, reference));
        var isAgentActivity = activity.State.IsAgentActivity(activity.ActivityId);
        var depth = _hierarchy.GetDepth(activity.State.AgentSessionId);
        var modelAliasIcon = _hierarchy.IsChild(activity.State.AgentSessionId) && isAgentActivity
            ? activity.State.ModelAliasIcon
            : null;
        return new HierarchicalLiveValue(
            value,
            isAgentActivity ? Math.Max(0, depth - 1) : depth,
            _hierarchy.GetLabel(activity.State.AgentSessionId),
            activity.State.Name,
            "♟",
            modelAliasIcon);
    }

    private bool IsOriginToolActive(ActiveShellProcess process) =>
        process.OriginToolCallId.Length > 0
        && _agentSessions.TryGetValue(process.OwnerAgentSessionId, out var owner)
        && owner.IsToolActive(process.OriginToolCallId);

    private ILiveBufferItem CreateQueueItem(QueueLiveBufferItem queue)
    {
        if (queue.OwnerAgentSessionId.Length == 0 || _hierarchy.IsRoot(queue.OwnerAgentSessionId))
        {
            return queue;
        }

        var owner = _hierarchy.GetLabel(queue.OwnerAgentSessionId) ?? queue.OwnerAgentSessionId;
        return new HierarchicalLiveValue(
            queue,
            _hierarchy.GetDepth(queue.OwnerAgentSessionId),
            owner,
            owner,
            null,
            null);
    }

    private HierarchicalLiveValue CreateNeutralAgentItem(AgentSessionState state) =>
        new(
            new NeutralAgentLiveBufferItem(state.Name),
            Math.Max(0, _hierarchy.GetDepth(state.AgentSessionId) - 1),
            _hierarchy.GetLabel(state.AgentSessionId),
            state.Name,
            "♟",
            null);

    private HierarchicalLiveValue CreateProcessItem(ProcessState process)
    {
        var owner = process.Process.OwnerAgentName.Length == 0
            ? process.Process.OwnerAgentSessionId
            : process.Process.OwnerAgentName;
        return new HierarchicalLiveValue(
            new ShellProcessLiveValue(process.Process, process.ObservedTimestamp, _timeProvider, _frame),
            Math.Max(0, process.Process.Depth),
            process.Process.Depth == 0 ? null : owner,
            owner,
            null,
            null);
    }

    private HierarchicalScrollbackValue Wrap(
        AgentSessionState state,
        IScrollbackItem value,
        string? successfulIcon) =>
        new(
            value,
            _hierarchy.GetDepth(state.AgentSessionId),
            _hierarchy.GetLabel(state.AgentSessionId),
            state.Name,
            successfulIcon);

    private async Task CommitCompletion(
        AgentSessionState state,
        (string ActivityId, string Response, string Line) completion,
        CancellationToken cancellationToken)
    {
        _ = _activities.Remove((state, completion.ActivityId));
        if (completion.Response.Length > 0)
        {
            var scrollbackValue = ToolYamlFormatter.TryFormat(completion.Response, out var yaml)
                ? new MarkdownScrollbackValue($"```yaml\n{yaml}\n```")
                : new MarkdownScrollbackValue(completion.Response);
            await commit(
                Wrap(state, scrollbackValue, null),
                Snapshot(),
                cancellationToken).ConfigureAwait(false);
        }

        await commit(
            Wrap(state, ImmediateScrollbackValue.Muted([completion.Line]), "♟"),
            Snapshot(),
            cancellationToken).ConfigureAwait(false);
    }

    private bool ObserveDeferredProcess(
        YieldedShellProcess yielded,
        Event published,
        ToolCallPresentation call,
        string command)
    {
        if (_retiredInventoryInstances.Contains(yielded.InventoryInstanceId))
        {
            return false;
        }

        if (_completedProcesses.Contains(ProcessKey(yielded.InventoryInstanceId, yielded.ProcessId)))
        {
            return true;
        }

        if (string.Equals(_inventoryInstanceId, yielded.InventoryInstanceId, StringComparison.Ordinal))
        {
            if (_processes.TryGetValue(yielded.ProcessId, out var observed))
            {
                _processes[yielded.ProcessId] = observed.Defer(command, yielded.VisibleRevision);
                return false;
            }

            if (_inventoryRevision > yielded.VisibleRevision)
            {
                return true;
            }
        }

        if (_inventoryInstanceId is not null
            && !string.Equals(_inventoryInstanceId, yielded.InventoryInstanceId, StringComparison.Ordinal))
        {
            _ = _retiredInventoryInstances.Add(_inventoryInstanceId);
            _processes.Clear();
            _completedProcesses.Clear();
            _omittedProcessTools.Clear();
            _terminalProcessTools.Clear();
            _inventoryRevision = 0;
        }

        _inventoryInstanceId = yielded.InventoryInstanceId;
        var process = new ActiveShellProcess
        {
            ProcessId = yielded.ProcessId,
            Name = yielded.Name,
            Command = command,
            OriginToolCallId = published.ToolFinished.ToolCallId,
            OwnerAgentSessionId = published.AgentSessionId,
            OwnerAgentName = call.Owner,
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

    private readonly record struct NeutralAgentLiveBufferItem(string Name) : ILiveBufferItem
    {
        public MultiLine Render(LiveBufferRenderContext context) => new(
            [new TerminalLine(
                TerminalText.Clip($"♟ agent {TerminalText.Sanitize(Name)}", context.Columns),
                context.Palette.LiveMuted)],
            null,
            LiveBufferRetention.Fixed);
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
}

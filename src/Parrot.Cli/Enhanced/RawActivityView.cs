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
            }

            var future = _processes.Values
                .Where(process =>
                    string.Equals(process.InventoryInstanceId, snapshot.InventoryInstanceId, StringComparison.Ordinal)
                    && process.Revision > snapshot.Revision)
                .ToArray();
            _inventoryInstanceId = snapshot.InventoryInstanceId;
            _inventoryRevision = snapshot.Revision;
            _processes.Clear();
            foreach (var process in snapshot.Processes)
            {
                _processes[process.ProcessId] = new ProcessState(
                    process.Clone(),
                    snapshot.InventoryInstanceId,
                    snapshot.Revision,
                    _timeProvider.GetTimestamp());
            }

            foreach (var process in future)
            {
                _processes[process.Process.ProcessId] = process;
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
            _hierarchy.Observe(published);
            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.AgentStarted:
                    await UpdateAgentName(
                        published.AgentSessionId,
                        published.AgentStarted.Name,
                        cancellationToken).ConfigureAwait(false);
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
                case Event.PayloadOneofCase.ReasoningChunk when _hierarchy.IsRoot(published.AgentSessionId):
                {
                    var fragment = TerminalText.Sanitize(published.ReasoningChunk.Fragment);
                    if (published.ReasoningChunk.Kind == ReasoningKind.Summary)
                    {
                        _ = _reasoning.Clear();
                        if (fragment.Length > 0)
                        {
                            await commit(
                                new ReasoningSummaryScrollbackValue(fragment),
                                Snapshot(),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    else
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
        var order = _hierarchy.GetPostOrder(activities.Select(static activity => activity.State.AgentSessionId));
        items.AddRange(activities
            .OrderBy(activity => order[activity.State.AgentSessionId])
            .ThenBy(activity => activity.State.IsAgentActivity(activity.ActivityId) ? 1 : 0)
            .ThenBy(static activity => activity.ActivityId, StringComparer.Ordinal)
            .Select(CreateActivityItem));
        items.AddRange(_processes.Values
            .Where(process => !IsOriginToolActive(process.Process))
            .OrderBy(static process => process.Process.Depth)
            .ThenBy(static process => process.Process.OwnerAgentName, StringComparer.Ordinal)
            .ThenBy(static process => process.Process.Name, StringComparer.Ordinal)
            .ThenBy(static process => process.Process.ProcessId, StringComparer.Ordinal)
            .Select(CreateProcessItem));
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
        static string ReadCommand(string argumentsJson)
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

        var state = GetNamedAgentSession(published.AgentSessionId);
        var (activityId, scrollback, call, terminal) = state.FinishTool(published, presenters);
        _ = _activities.Remove((state, activityId));
        if (terminal.YieldedProcess is { } yielded
            && !_retiredInventoryInstances.Contains(yielded.InventoryInstanceId)
            && (_inventoryInstanceId is null
                || !string.Equals(_inventoryInstanceId, yielded.InventoryInstanceId, StringComparison.Ordinal)
                || _inventoryRevision < yielded.VisibleRevision))
        {
            if (_inventoryInstanceId is not null
                && !string.Equals(_inventoryInstanceId, yielded.InventoryInstanceId, StringComparison.Ordinal))
            {
                _ = _retiredInventoryInstances.Add(_inventoryInstanceId);
                _processes.Clear();
                _inventoryRevision = 0;
            }

            _inventoryInstanceId = yielded.InventoryInstanceId;
            var process = new ActiveShellProcess
            {
                ProcessId = yielded.ProcessId,
                Name = yielded.Name,
                Command = ReadCommand(call.ArgumentsJson),
                OriginToolCallId = published.ToolFinished.ToolCallId,
                OwnerAgentSessionId = published.AgentSessionId,
                OwnerAgentName = call.Owner,
                ParentAgentSessionId = string.Empty,
                ParentAgentName = string.Empty,
                Depth = _hierarchy.GetDepth(published.AgentSessionId),
                ElapsedMs = 0,
            };
            _ = _processes.TryAdd(
                yielded.ProcessId,
                new ProcessState(
                    process,
                    yielded.InventoryInstanceId,
                    yielded.VisibleRevision,
                    _timeProvider.GetTimestamp()));
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

    private ILiveBufferItem CreateActivityItem((AgentSessionState State, string ActivityId) activity)
    {
        var value = activity.State.CreateLiveBufferItem(activity.ActivityId, _frame, presenters);
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

    private ILiveBufferItem CreateProcessItem(ProcessState process)
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
            await commit(
                Wrap(state, new MarkdownScrollbackValue(completion.Response), null),
                Snapshot(),
                cancellationToken).ConfigureAwait(false);
        }

        await commit(
            Wrap(state, ImmediateScrollbackValue.Muted([completion.Line]), "♟"),
            Snapshot(),
            cancellationToken).ConfigureAwait(false);
    }

    private sealed record ProcessState(
        ActiveShellProcess Process,
        string InventoryInstanceId,
        ulong Revision,
        long ObservedTimestamp);
}

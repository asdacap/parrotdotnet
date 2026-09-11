using System.Globalization;
using System.Text;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentSessionState(string agentSessionId)
{
    internal const string AgentActivityId = "agent";
    private const string CompactionActivity = "compaction";
    private const int MaximumResponseLines = 10;
    private const string ToolActivityPrefix = "tool:";

    private readonly HashSet<string> _activities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, StringBuilder Arguments)> _toolCalls =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, (string ToolName, long Order)> _foldedTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ILiveBufferItem> _toolLive = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ulong> _toolProgressRevisions = new(StringComparer.Ordinal);
    private readonly HashSet<string> _terminalTools = new(StringComparer.Ordinal);
    private readonly HashSet<string> _detachedAgentTasks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _terminalAgentTaskProgress = new(StringComparer.Ordinal);
    private readonly StringBuilder _response = new();

    private bool _terminalCommitted;
    private uint _requestAttempt;
    private bool _waitingForFirstToken;
    private bool _agentTerminalPending;
    private bool _responseComplete;
    private string? _name;
    private AgentStatisticsUpdatedEvent? _statistics;
    private int _responseLineBreaks;
    private long _foldedToolOrder;

    public bool HasName => _name is not null;

    public string Name => _name ?? agentSessionId;

    public string AgentSessionId => agentSessionId;

    public string AgentLabel => CreateAgentLabel();

    public string ModelineLabel => $"agent {Name}";

    public bool IsAgentActive => _activities.Contains(AgentActivityId);

    public LiveModelAliasIcon? ModelAliasIcon { get; private set; }

    public void UpdateName(string name) => _name = name;

    public void UpdateStatistics(AgentStatisticsUpdatedEvent statistics) => _statistics = statistics;

    public void ObserveRequestPhase(ProviderRequestPhaseChangedEvent update)
    {
        _waitingForFirstToken = IsAgentActive && update.Phase == ProviderRequestPhase.HeadersReceived;
        _requestAttempt = IsAgentActive && update.Phase == ProviderRequestPhase.Requesting
            ? Math.Max(1u, update.Attempt)
            : 0;
    }

    public string? StartTurn(LiveModelAliasIcon? modelAliasIcon)
    {
        if (!_activities.Add(AgentActivityId))
        {
            return null;
        }

        _requestAttempt = 0;
        _waitingForFirstToken = false;
        ModelAliasIcon = modelAliasIcon;
        _terminalCommitted = false;
        _agentTerminalPending = true;
        _foldedTools.Clear();
        _foldedToolOrder = 0;
        _ = _response.Clear();
        _responseComplete = false;
        _responseLineBreaks = 0;
        return AgentActivityId;
    }

    public string? StartCompaction() => _activities.Add(CompactionActivity) ? CompactionActivity : null;

    public (string ActivityId, string Line)? FinishCompaction(Event published)
    {
        if (!_activities.Remove(CompactionActivity))
        {
            return null;
        }

        var line = published.PayloadCase == Event.PayloadOneofCase.CompactionFailed
            ? $"! compaction failed: {TerminalText.Sanitize(published.CompactionFailed.Message)}"
            : "+ compaction finished";
        return (CompactionActivity, line);
    }

    public void CollectResponse(string fragment)
    {
        if (_terminalCommitted)
        {
            return;
        }

        foreach (var character in TerminalText.Sanitize(fragment))
        {
            if (_responseComplete)
            {
                return;
            }

            if (character == '\n' && _responseLineBreaks == MaximumResponseLines - 1)
            {
                _responseComplete = true;
                return;
            }

            _ = _response.Append(character);
            if (character == '\n')
            {
                _responseLineBreaks++;
            }
        }
    }

    public async Task FlushResponse(Func<string, Task> flush)
    {
        ArgumentNullException.ThrowIfNull(flush);

        var responseComplete = _responseComplete;
        var responseLineBreaks = _responseLineBreaks;
        var response = DrainResponse();
        if (response.Length == 0)
        {
            return;
        }

        try
        {
            await flush(response).ConfigureAwait(false);
        }
        catch
        {
            _ = _response.Append(response);
            _responseComplete = responseComplete;
            _responseLineBreaks = responseLineBreaks;
            throw;
        }
    }

    public async Task<string?> FinishChildTurn(Func<string, Task> flush)
    {
        ArgumentNullException.ThrowIfNull(flush);

        if (!_activities.Remove(AgentActivityId))
        {
            return null;
        }

        try
        {
            await FlushResponse(flush).ConfigureAwait(false);
        }
        catch
        {
            _ = _activities.Add(AgentActivityId);
            throw;
        }

        _requestAttempt = 0;
        _waitingForFirstToken = false;
        _terminalCommitted = true;
        return AgentActivityId;
    }

    public (string ActivityId, string Response, string Line)? FinishTurn(Event published, bool failed)
    {
        if (!_activities.Remove(AgentActivityId))
        {
            return null;
        }

        _requestAttempt = 0;
        _waitingForFirstToken = false;
        _terminalCommitted = true;
        var interrupted = !failed
            && string.Equals(published.TurnEnded.FinishReason, "interrupted", StringComparison.Ordinal);
        var response = DrainResponse();
        var status = failed
            ? $"! agent: {TerminalText.Sanitize(published.TurnFailed.Message)}"
            : interrupted
                ? "- agent interrupted"
                : "+ agent finished";
        return (AgentActivityId, response, status);
    }

    public (string ActivityId, string Line)? FinishAgent(Event published, bool failed)
    {
        if (!_agentTerminalPending)
        {
            return null;
        }

        var status = failed
            ? $"! agent: {TerminalText.Sanitize(published.AgentFailed.Message)}"
            : $"+ agent finished ({AgentDurationFormatter.Format(published.AgentFinished.ElapsedMs)})";
        return (AgentActivityId, status);
    }

    public void CompleteAgent()
    {
        _agentTerminalPending = false;
        _requestAttempt = 0;
        _waitingForFirstToken = false;
        _terminalCommitted = true;
        _ = _activities.Remove(AgentActivityId);
        _ = DrainResponse();
    }

    public void CollectToolCall(ToolCallChunk chunk)
    {
        if (_terminalTools.Contains(chunk.ToolCallId))
        {
            return;
        }

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
        if (!_toolProgressRevisions.ContainsKey(chunk.ToolCallId))
        {
            _ = _toolLive.Remove(chunk.ToolCallId);
        }
    }

    public string? StartTool(ToolStarted tool, bool foldIntoAgentStatus)
    {
        var activityId = ToolActivityPrefix + tool.ToolCallId;
        if (_activities.Contains(activityId) || _terminalTools.Contains(tool.ToolCallId))
        {
            return null;
        }

        if (!_toolCalls.TryGetValue(tool.ToolCallId, out var toolCall))
        {
            toolCall = (tool.ToolName, new StringBuilder());
            _toolCalls.Add(tool.ToolCallId, toolCall);
        }
        else if (tool.ToolName.Length > 0)
        {
            toolCall.Name = tool.ToolName;
            _toolCalls[tool.ToolCallId] = toolCall;
        }

        _ = _toolLive.Remove(tool.ToolCallId);
        _ = _toolProgressRevisions.Remove(tool.ToolCallId);
        _ = _activities.Add(activityId);

        if (foldIntoAgentStatus)
        {
            _foldedTools[tool.ToolCallId] = (toolCall.Name, _foldedToolOrder++);
        }

        return activityId;
    }

    public bool IsFoldedActivity(string activityId) =>
        activityId.StartsWith(ToolActivityPrefix, StringComparison.Ordinal)
        && _foldedTools.ContainsKey(activityId[ToolActivityPrefix.Length..]);

    public bool IsToolActive(string toolCallId) => _activities.Contains(ToolActivityPrefix + toolCallId);

    public bool IsDetachedAgentTask(string toolCallId) => _detachedAgentTasks.Contains(toolCallId);

    public bool IsTerminalAgentTaskProgress(string toolCallId) =>
        _terminalAgentTaskProgress.Contains(toolCallId);

    public IReadOnlyList<string> DetachedAgentTaskProgressIds() =>
        [.. _detachedAgentTasks.Where(_toolLive.ContainsKey).Order(StringComparer.Ordinal)];

    public ILiveBufferItem CreateDetachedAgentTaskProgressItem(string toolCallId) =>
        _toolLive.TryGetValue(toolCallId, out var live)
            ? live
            : throw new InvalidOperationException($"AgentTask progress '{toolCallId}' is not available.");

    public void RefreshToolPresentations()
    {
        foreach (var toolCallId in _toolLive.Keys.Where(toolCallId => !_toolProgressRevisions.ContainsKey(toolCallId)).ToArray())
        {
            _ = _toolLive.Remove(toolCallId);
        }
    }

    public bool OfferAgentTaskProgress(AgentTaskProgressSnapshot snapshot)
    {
        var toolCallId = snapshot.OriginToolCallId;
        if ((!IsToolActive(toolCallId) && !_detachedAgentTasks.Contains(toolCallId))
            || !_toolCalls.TryGetValue(toolCallId, out var call)
            || !string.Equals(call.Name, "run_agent_tasks", StringComparison.Ordinal)
            || (_toolProgressRevisions.TryGetValue(toolCallId, out var revision) && snapshot.Revision <= revision))
        {
            return false;
        }

        _toolProgressRevisions[toolCallId] = snapshot.Revision;
        _toolLive[toolCallId] = new AgentTaskProgressLiveValue(snapshot.Clone());
        if (IsTerminal(snapshot))
        {
            _ = _terminalAgentTaskProgress.Add(toolCallId);
        }

        return true;
    }

    public bool IsCurrentAgentTaskProgress(string toolCallId, ulong revision) =>
        (IsToolActive(toolCallId) || _detachedAgentTasks.Contains(toolCallId))
        && _toolCalls.TryGetValue(toolCallId, out var call)
        && string.Equals(call.Name, "run_agent_tasks", StringComparison.Ordinal)
        && _toolProgressRevisions.TryGetValue(toolCallId, out var currentRevision)
        && currentRevision == revision;

    public bool RetireDetachedAgentTaskProgress(string toolCallId, ulong revision)
    {
        if (!_detachedAgentTasks.Contains(toolCallId)
            || !_terminalAgentTaskProgress.Contains(toolCallId)
            || !IsCurrentAgentTaskProgress(toolCallId, revision))
        {
            return false;
        }

        _ = _detachedAgentTasks.Remove(toolCallId);
        _ = _terminalAgentTaskProgress.Remove(toolCallId);
        _ = _toolProgressRevisions.Remove(toolCallId);
        _ = _toolLive.Remove(toolCallId);
        _ = _toolCalls.Remove(toolCallId);
        return true;
    }

    public (string ActivityId, IScrollbackItem? Scrollback, ToolCallPresentation Call, ToolTerminalPresentation Terminal) FinishTool(
        Event published,
        ToolPresenterRegistry presenters,
        Func<string, string> agentReferenceResolver)
    {
        var (toolCallId, toolName) = GetTerminalTool(published);
        var isAgentTask = string.Equals(toolName, "run_agent_tasks", StringComparison.Ordinal)
            || (_toolCalls.TryGetValue(toolCallId, out var knownCall)
                && string.Equals(knownCall.Name, "run_agent_tasks", StringComparison.Ordinal));
        var retainBackgroundTask = isAgentTask
            && _toolCalls.ContainsKey(toolCallId)
            && published.PayloadCase == Event.PayloadOneofCase.ToolFinished
            && !_terminalAgentTaskProgress.Contains(toolCallId);
        if (retainBackgroundTask)
        {
            _ = _detachedAgentTasks.Add(toolCallId);
        }
        else
        {
            _ = _toolLive.Remove(toolCallId);
            _ = _toolProgressRevisions.Remove(toolCallId);
            _ = _detachedAgentTasks.Remove(toolCallId);
            _ = _terminalAgentTaskProgress.Remove(toolCallId);
        }

        _ = _terminalTools.Add(toolCallId);
        var retainDetachedProgress = isAgentTask
            && _detachedAgentTasks.Contains(toolCallId)
            && !_terminalAgentTaskProgress.Contains(toolCallId);
        (string Name, StringBuilder Arguments) toolCall;
        if (retainDetachedProgress)
        {
            toolCall = _toolCalls[toolCallId];
            if (toolCall.Name.Length == 0)
            {
                toolCall.Name = toolName;
                _toolCalls[toolCallId] = toolCall;
            }
        }
        else if (!_toolCalls.Remove(toolCallId, out toolCall))
        {
            toolCall = (toolName, new StringBuilder());
            _ = _detachedAgentTasks.Remove(toolCallId);
            _ = _terminalAgentTaskProgress.Remove(toolCallId);
            _ = _toolProgressRevisions.Remove(toolCallId);
        }
        else if (toolCall.Name.Length == 0)
        {
            toolCall.Name = toolName;
        }

        var activityId = ToolActivityPrefix + toolCallId;
        _ = _activities.Remove(activityId);
        _ = _foldedTools.Remove(toolCallId);
        var call = new ToolCallPresentation(Name, toolCall.Name, toolCall.Arguments.ToString(), agentReferenceResolver);
        var terminal = published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolFinished => new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                published.ToolFinished.HasResult,
                published.ToolFinished.Result,
                string.Empty,
                published.ToolFinished.YieldedProcess),
            Event.PayloadOneofCase.ToolCancelled => new ToolTerminalPresentation(
                ToolTerminalStatus.Cancelled,
                false,
                string.Empty,
                string.Empty),
            Event.PayloadOneofCase.ToolError => new ToolTerminalPresentation(
                ToolTerminalStatus.Errored,
                false,
                string.Empty,
                published.ToolError.Message),
            _ => throw new InvalidOperationException("The tool event is not terminal."),
        };
        return (activityId, presenters.PresentTerminal(call, terminal), call, terminal);
    }

    public bool IsAgentActivity(string activityId) =>
        _activities.Contains(AgentActivityId) && string.Equals(activityId, AgentActivityId, StringComparison.Ordinal);

    public ILiveBufferItem CreateLiveBufferItem(
        string activityId,
        int frame,
        ToolPresenterRegistry presenters,
        Func<string, string> agentReferenceResolver)
    {
        if (IsAgentActivity(activityId))
        {
            return _waitingForFirstToken
                ? new SpinnerValue($"{AgentLabel} Waiting for first token…", frame)
                : _requestAttempt > 0
                    ? new SpinnerValue(
                        _requestAttempt == 1
                            ? $"{AgentLabel} Requesting…"
                            : $"{AgentLabel} Requesting (attempt {_requestAttempt})…",
                        frame)
                    : _response.Length == 0
                        ? new SpinnerValue(AgentLabel, frame)
                        : new StreamedResponseValue("● ", _response.ToString());
        }

        if (string.Equals(activityId, CompactionActivity, StringComparison.Ordinal))
        {
            return new SpinnerValue("Compacting…", frame);
        }

        var toolCallId = activityId[ToolActivityPrefix.Length..];
        if (!_toolLive.TryGetValue(toolCallId, out var live))
        {
            var toolCall = _toolCalls[toolCallId];
            live = presenters.PresentLive(
                new ToolCallPresentation(Name, toolCall.Name, toolCall.Arguments.ToString(), agentReferenceResolver),
                frame);
            _toolLive.Add(toolCallId, live);
        }

        return live.Animate(frame);
    }

    private static bool IsTerminal(AgentTaskProgressSnapshot snapshot) =>
        snapshot.RootNodes.Count > 0 && snapshot.RootNodes.All(IsTerminal);

    private static bool IsTerminal(AgentTaskProgressNode node) =>
        node.Status is AgentTaskProgressStatus.Succeeded
            or AgentTaskProgressStatus.Failed
            or AgentTaskProgressStatus.Blocked
            or AgentTaskProgressStatus.Canceled
        && node.Children.All(IsTerminal);

    private static (string ToolCallId, string ToolName) GetTerminalTool(Event published) =>
        published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolFinished =>
                (published.ToolFinished.ToolCallId, published.ToolFinished.ToolName),
            Event.PayloadOneofCase.ToolCancelled =>
                (published.ToolCancelled.ToolCallId, published.ToolCancelled.ToolName),
            Event.PayloadOneofCase.ToolError =>
                (published.ToolError.ToolCallId, published.ToolError.ToolName),
            _ => (string.Empty, string.Empty),
        };

    private static string FormatTokenCount(long count) => count switch
    {
        >= 1_000_000 => $"{(count / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture)}m",
        >= 1_000 => $"{(count / 1_000d).ToString("0.#", CultureInfo.InvariantCulture)}k",
        _ => count.ToString(CultureInfo.InvariantCulture),
    };

    private static string FormatContextLimit(long limit) => limit == 0 ? "?" : FormatTokenCount(limit);

    private string DrainResponse()
    {
        var response = _response.ToString();
        _ = _response.Clear();
        _responseComplete = false;
        _responseLineBreaks = 0;
        return response;
    }

    private string CreateAgentLabel()
    {
        var label = _statistics is { } statistics
            ? $"agent {Name} ({FormatTokenCount(statistics.InputTokens)} in / " +
              $"{FormatTokenCount(statistics.CachedInputTokens)} cached / " +
              $"{FormatTokenCount(statistics.OutputTokens)} out, " +
              $"{FormatTokenCount(statistics.ContextSize)}/{FormatContextLimit(statistics.ContextLimit)} ctx)"
            : $"agent {Name}";
        return _foldedTools.Count == 0
            ? label
            : $"{label} Working: {LatestFoldedTool()}";
    }

    private string LatestFoldedTool() => _foldedTools.Values
        .OrderByDescending(static tool => tool.Order)
        .ThenBy(static tool => tool.ToolName, StringComparer.Ordinal)
        .First()
        .ToolName;
}

using System.Globalization;
using System.Text;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Context;
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
    private readonly HashSet<string> _terminalTools = new(StringComparer.Ordinal);
    private readonly StringBuilder _response = new();
    private readonly StringBuilder _reasoning = new();

    private ulong _agentTaskRevision;
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

    public string AgentSpinnerText => _waitingForFirstToken
        ? $"{AgentLabel} Waiting for first token…"
        : _requestAttempt switch
        {
            0 => AgentLabel,
            1 => $"{AgentLabel} Requesting…",
            _ => $"{AgentLabel} Requesting (attempt {_requestAttempt})…",
        };

    public bool IsStreamingResponse => !_waitingForFirstToken && _requestAttempt == 0 && _response.Length > 0;

    public string ModelineLabel => $"agent {Name}";

    public bool IsAgentActive => _activities.Contains(AgentActivityId);

    public LiveModelAliasIcon? ModelAliasIcon { get; private set; }

    /// <summary>Gets the agent's current task graph, until a terminal revision of it is retired.</summary>
    public AgentTaskProgressSnapshot? AgentTaskProgress { get; private set; }

    public bool HasReasoning => _reasoning.Length > 0;

    public bool IsReasoningSummary { get; private set; }

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
        _ = _reasoning.Clear();
        _responseComplete = false;
        _responseLineBreaks = 0;
        return AgentActivityId;
    }

    public string? StartCompaction() => _activities.Add(CompactionActivity) ? CompactionActivity : null;

    public (string ActivityId, ActivityNoticeScrollbackValue Notice)? FinishCompaction(Event published)
    {
        if (!_activities.Remove(CompactionActivity))
        {
            return null;
        }

        var notice = published.PayloadCase == Event.PayloadOneofCase.CompactionFailed
            ? new ActivityNoticeScrollbackValue(
                TerminalIcons.Failure,
                $"compaction failed: {published.CompactionFailed.Message}")
            : new ActivityNoticeScrollbackValue(TerminalIcons.Success, "compaction finished");
        return (CompactionActivity, notice);
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

    public void CollectReasoning(ReasoningChunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);

        IsReasoningSummary = chunk.Kind == ReasoningKind.Summary;
        _ = _reasoning.Append(TerminalText.Sanitize(chunk.Fragment));
    }

    public ILiveBufferItem CreateReasoningItem(int frame) => IsReasoningSummary
        ? new StreamedResponseValue(TerminalIcons.Reasoning, _reasoning.ToString())
        : new SpinnerValue(
            $"Thinking ({FormatTokenCount(TokenEstimator.EstimateTokens(_reasoning.ToString()))} tokens)…",
            frame);

    public async Task FlushReasoning(Func<IScrollbackItem, Task> flush)
    {
        ArgumentNullException.ThrowIfNull(flush);

        var reasoning = _reasoning.ToString();
        _ = _reasoning.Clear();
        if (IsBlank(reasoning))
        {
            return;
        }

        try
        {
            await flush(CreateReasoningScrollback(reasoning)).ConfigureAwait(false);
        }
        catch
        {
            _ = _reasoning.Insert(0, reasoning);
            throw;
        }
    }

    public async Task FlushResponse(Func<string, Task> flush)
    {
        ArgumentNullException.ThrowIfNull(flush);

        var responseComplete = _responseComplete;
        var responseLineBreaks = _responseLineBreaks;
        var response = DrainResponse();
        if (IsBlank(response))
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

    public (string ActivityId, string Response, ActivityNoticeScrollbackValue Notice)? FinishTurn(
        Event published,
        bool failed)
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
        var notice = failed
            ? new ActivityNoticeScrollbackValue(TerminalIcons.Failure, $"agent: {published.TurnFailed.Message}")
            : interrupted
                ? new ActivityNoticeScrollbackValue(TerminalIcons.Interrupted, "agent interrupted")
                : new ActivityNoticeScrollbackValue(TerminalIcons.Agent, "agent finished");
        return (AgentActivityId, IsBlank(response) ? string.Empty : response, notice);
    }

    public (string ActivityId, ActivityNoticeScrollbackValue Notice)? FinishAgent(Event published, bool failed)
    {
        if (!_agentTerminalPending)
        {
            return null;
        }

        var notice = failed
            ? new ActivityNoticeScrollbackValue(TerminalIcons.Failure, $"agent: {published.AgentFailed.Message}")
            : new ActivityNoticeScrollbackValue(
                TerminalIcons.Agent,
                $"agent finished after {AgentDurationFormatter.Format(published.AgentFinished.ElapsedMs)}");
        return (AgentActivityId, notice);
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
        _ = _toolLive.Remove(chunk.ToolCallId);
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
        _ = _activities.Add(activityId);

        if (foldIntoAgentStatus)
        {
            _foldedTools[tool.ToolCallId] = (toolCall.Name, _foldedToolOrder++);
        }

        return activityId;
    }

    public IScrollbackItem? PresentStartedTool(
        string toolCallId,
        ToolPresenterRegistry presenters,
        Func<string, string> agentReferenceResolver,
        bool isRoot)
    {
        var toolCall = _toolCalls[toolCallId];
        var call = new ToolCallPresentation(toolCall.Name, toolCall.Arguments.ToString(), agentReferenceResolver);
        return presenters.PresentStarted(call) ?? (isRoot ? null : presenters.PresentChildStarted(call));
    }

    public bool IsFoldedActivity(string activityId) =>
        activityId.StartsWith(ToolActivityPrefix, StringComparison.Ordinal)
        && _foldedTools.ContainsKey(activityId[ToolActivityPrefix.Length..]);

    public bool IsToolActive(string toolCallId) => _activities.Contains(ToolActivityPrefix + toolCallId);

    public void RefreshToolPresentations() => _toolLive.Clear();

    public bool OfferAgentTaskProgress(AgentTaskProgressSnapshot snapshot)
    {
        if (snapshot.Revision <= _agentTaskRevision)
        {
            return false;
        }

        _agentTaskRevision = snapshot.Revision;
        AgentTaskProgress = snapshot.Clone();
        return true;
    }

    public bool IsCurrentAgentTaskProgress(ulong revision) =>
        AgentTaskProgress is not null && _agentTaskRevision == revision;

    public bool RetireAgentTaskProgress(ulong revision)
    {
        if (AgentTaskProgress is not { } tasks || _agentTaskRevision != revision || !IsTerminal(tasks))
        {
            return false;
        }

        AgentTaskProgress = null;
        return true;
    }

    public (string ActivityId, IScrollbackItem? Scrollback, ToolCallPresentation Call, ToolTerminalPresentation Terminal) FinishTool(
        Event published,
        ToolPresenterRegistry presenters,
        Func<string, string> agentReferenceResolver,
        bool startOmitted)
    {
        var (toolCallId, toolName) = GetTerminalTool(published);
        _ = _toolLive.Remove(toolCallId);
        _ = _terminalTools.Add(toolCallId);
        if (!_toolCalls.Remove(toolCallId, out var toolCall))
        {
            toolCall = (toolName, new StringBuilder());
        }
        else if (toolCall.Name.Length == 0)
        {
            toolCall.Name = toolName;
        }

        var activityId = ToolActivityPrefix + toolCallId;
        _ = _activities.Remove(activityId);
        _ = _foldedTools.Remove(toolCallId);
        var call = new ToolCallPresentation(toolCall.Name, toolCall.Arguments.ToString(), agentReferenceResolver);
        var terminal = published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolFinished => new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                published.ToolFinished.HasResult,
                published.ToolFinished.Result,
                string.Empty,
                published.ToolFinished.YieldedProcess,
                published.ToolFinished.Artifacts),
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
        terminal = terminal with { StartOmitted = startOmitted };
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
            return IsStreamingResponse
                ? new StreamedResponseValue(TerminalIcons.AssistantMessage, _response.ToString())
                : new SpinnerValue(AgentSpinnerText, frame);
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
                new ToolCallPresentation(toolCall.Name, toolCall.Arguments.ToString(), agentReferenceResolver),
                frame);
            _toolLive.Add(toolCallId, live);
        }

        return live.Animate(frame);
    }

    private static bool IsTerminal(AgentTaskProgressSnapshot snapshot) =>
        snapshot.RootNodes.Count > 0 && snapshot.RootNodes.All(IsTerminal);

    private static bool IsTerminal(AgentTaskProgressNode node) =>
        node.Status is AgentTaskProgressStatus.Succeeded
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

    private static bool IsBlank(string response) => string.IsNullOrWhiteSpace(response);

    private IScrollbackItem CreateReasoningScrollback(string reasoning) => IsReasoningSummary
        ? new ReasoningSummaryScrollbackValue(reasoning)
        : new ActivityNoticeScrollbackValue(
            TerminalIcons.Reasoning,
            $"Reasoned for {FormatTokenCount(TokenEstimator.EstimateTokens(reasoning))} tokens…");

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

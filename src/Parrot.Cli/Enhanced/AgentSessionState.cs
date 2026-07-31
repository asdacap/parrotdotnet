using System.Globalization;
using System.Text;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentSessionState(string agentSessionId)
{
    private const string AgentActivity = "agent";
    private const int MaximumResponseLines = 10;
    private const string ToolActivityPrefix = "tool:";

    private readonly HashSet<string> _activities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, StringBuilder Arguments)> _toolCalls =
        new(StringComparer.Ordinal);

    private readonly Dictionary<string, (string ToolName, long Order)> _foldedTools = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ILiveBufferItem> _toolLive = new(StringComparer.Ordinal);
    private readonly StringBuilder _response = new();

    private bool _terminalCommitted;
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

    public LiveModelAliasIcon? ModelAliasIcon { get; private set; }

    public void UpdateName(string name) => _name = name;

    public void UpdateStatistics(AgentStatisticsUpdatedEvent statistics) => _statistics = statistics;

    public string? StartTurn(LiveModelAliasIcon? modelAliasIcon)
    {
        if (!_activities.Add(AgentActivity))
        {
            return null;
        }

        ModelAliasIcon = modelAliasIcon;
        _terminalCommitted = false;
        _foldedTools.Clear();
        _foldedToolOrder = 0;
        _ = _response.Clear();
        _responseComplete = false;
        _responseLineBreaks = 0;
        return AgentActivity;
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

    public (string ActivityId, string Response, string Line)? FinishTurn(Event published, bool failed)
    {
        if (!_activities.Remove(AgentActivity))
        {
            return null;
        }

        _terminalCommitted = true;
        var interrupted = !failed
            && string.Equals(published.TurnEnded.FinishReason, "interrupted", StringComparison.Ordinal);
        var response = _response.ToString();
        _ = _response.Clear();
        _responseComplete = false;
        _responseLineBreaks = 0;
        var status = failed
            ? $"! agent: {TerminalText.Sanitize(published.TurnFailed.Message)}"
            : interrupted
                ? "- agent interrupted"
                : "+ agent finished";
        return (AgentActivity, response, status);
    }

    public (string ActivityId, string Response, string Line)? FinishAgent(Event published, bool failed)
    {
        if (_terminalCommitted || !_activities.Remove(AgentActivity))
        {
            return null;
        }

        _terminalCommitted = true;
        var response = _response.ToString();
        _ = _response.Clear();
        _responseComplete = false;
        _responseLineBreaks = 0;
        var status = failed
            ? $"! agent: {TerminalText.Sanitize(published.AgentFailed.Message)}"
            : "+ agent finished";
        return (AgentActivity, response, status);
    }

    public void CollectToolCall(ToolCallChunk chunk)
    {
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
        var activityId = ToolActivityPrefix + tool.ToolCallId;
        if (!_activities.Add(activityId))
        {
            return null;
        }

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

    public (string ActivityId, IScrollbackItem? Scrollback, ToolCallPresentation Call, ToolTerminalPresentation Terminal) FinishTool(
        Event published,
        ToolPresenterRegistry presenters)
    {
        var (toolCallId, toolName) = GetTerminalTool(published);
        _ = _toolLive.Remove(toolCallId);
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
        var call = new ToolCallPresentation(Name, toolCall.Name, toolCall.Arguments.ToString());
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
        _activities.Contains(AgentActivity) && string.Equals(activityId, AgentActivity, StringComparison.Ordinal);

    public ILiveBufferItem CreateLiveBufferItem(
        string activityId,
        int frame,
        ToolPresenterRegistry presenters)
    {
        if (IsAgentActivity(activityId))
        {
            return _response.Length == 0
                ? new SpinnerValue(AgentLabel, frame)
                : new MarqueeValue("● ", _response.ToString(), frame);
        }

        var toolCallId = activityId[ToolActivityPrefix.Length..];
        if (!_toolLive.TryGetValue(toolCallId, out var live))
        {
            var toolCall = _toolCalls[toolCallId];
            live = presenters.PresentLive(
                new ToolCallPresentation(Name, toolCall.Name, toolCall.Arguments.ToString()),
                frame);
            _toolLive.Add(toolCallId, live);
        }

        return live is ToolLiveValue value ? value.Animate(frame) : live;
    }

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

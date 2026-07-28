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

    private readonly Dictionary<string, ILiveBufferItem> _toolLive = new(StringComparer.Ordinal);
    private readonly StringBuilder _response = new();

    private bool _terminalCommitted;
    private bool _responseComplete;
    private string? _name;
    private AgentStatisticsUpdatedEvent? _statistics;
    private int _responseLineBreaks;

    public bool HasName => _name is not null;

    public string Name => _name ?? agentSessionId;

    public string AgentSessionId => agentSessionId;

    public void UpdateName(string name) => _name = name;

    public void UpdateStatistics(AgentStatisticsUpdatedEvent statistics) => _statistics = statistics;

    public string? StartTurn()
    {
        if (!_activities.Add(AgentActivity))
        {
            return null;
        }

        _terminalCommitted = false;
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

    public string? StartTool(ToolStarted tool)
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
        return _activities.Add(activityId) ? activityId : null;
    }

    public (string ActivityId, IScrollbackItem? Scrollback) FinishTool(
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
        var call = new ToolCallPresentation(Name, toolCall.Name, toolCall.Arguments.ToString());
        var terminal = published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolFinished => new ToolTerminalPresentation(
                ToolTerminalStatus.Succeeded,
                published.ToolFinished.HasResult,
                published.ToolFinished.Result,
                string.Empty),
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
        return (activityId, presenters.PresentTerminal(call, terminal));
    }

    public ILiveBufferItem CreateLiveBufferItem(
        string activityId,
        int frame,
        ToolPresenterRegistry presenters)
    {
        if (string.Equals(activityId, AgentActivity, StringComparison.Ordinal))
        {
            return _response.Length == 0
                ? new SpinnerValue(CreateAgentLabel(), frame)
                : new LiveTextValue($"● {_response}");
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

    private string CreateAgentLabel() => _statistics is { } statistics
        ? $"agent {Name} ({FormatTokenCount(statistics.InputTokens)} in / " +
          $"{FormatTokenCount(statistics.CachedInputTokens)} cached / " +
          $"{FormatTokenCount(statistics.OutputTokens)} out, " +
          $"{FormatTokenCount(statistics.ContextSize)}/{FormatContextLimit(statistics.ContextLimit)} ctx)"
        : $"agent {Name}";
}

using System.Globalization;
using System.Text;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class AgentSessionState(string agentSessionId)
{
    private const string AgentActivity = "agent";
    private const string ToolActivityPrefix = "tool:";

    private readonly HashSet<string> _activities = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Name, StringBuilder Arguments)> _toolCalls =
        new(StringComparer.Ordinal);

    private bool _completedTurn;
    private string? _name;
    private AgentStatisticsUpdatedEvent? _statistics;

    public bool HasName => _name is not null;

    private string Name => _name ?? agentSessionId;

    public void UpdateName(string name) => _name = name;

    public void UpdateStatistics(AgentStatisticsUpdatedEvent statistics) => _statistics = statistics;

    public string? StartTurn() => _activities.Add(AgentActivity) ? AgentActivity : null;

    public (string ActivityId, string Line)? FinishTurn(Event published, bool failed)
    {
        if (!_activities.Remove(AgentActivity))
        {
            return null;
        }

        _completedTurn = true;
        var interrupted = !failed
            && string.Equals(published.TurnEnded.FinishReason, "interrupted", StringComparison.Ordinal);
        var line = failed
            ? $"! agent {Name}: {TerminalText.Sanitize(published.TurnFailed.Message)}"
            : interrupted
                ? $"- agent {Name} interrupted"
                : $"+ agent {Name} finished";
        return (AgentActivity, line);
    }

    public (string ActivityId, string Line)? FinishAgent(Event published, bool failed)
    {
        if (_completedTurn)
        {
            _completedTurn = false;
            return null;
        }

        _ = _activities.Remove(AgentActivity);
        var name = failed ? published.AgentFailed.Name : published.AgentFinished.Name;
        var line = failed
            ? $"! agent {name}: {TerminalText.Sanitize(published.AgentFailed.Message)}"
            : $"+ agent {name} finished";
        return (AgentActivity, line);
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

        var activityId = ToolActivityPrefix + tool.ToolCallId;
        return _activities.Add(activityId) ? activityId : null;
    }

    public (string ActivityId, string Line) FinishTool(Event published)
    {
        var (toolCallId, toolName) = GetTerminalTool(published);
        if (!_toolCalls.Remove(toolCallId, out var toolCall))
        {
            toolCall = (toolName, new StringBuilder());
        }

        var activityId = ToolActivityPrefix + toolCallId;
        _ = _activities.Remove(activityId);
        var description = DescribeToolCall(toolCall);
        var line = published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolFinished => $"+ {Name}: {description}",
            Event.PayloadOneofCase.ToolCancelled => $"- {Name}: {description} cancelled",
            Event.PayloadOneofCase.ToolError =>
                $"! {Name}: {description}: {TerminalText.Sanitize(published.ToolError.Message)}",
            _ => string.Empty,
        };
        return (activityId, line);
    }

    public ILiveBufferItem CreateLiveBufferItem(string activityId, int frame)
    {
        var label = string.Equals(activityId, AgentActivity, StringComparison.Ordinal)
            ? CreateAgentLabel()
            : CreateToolLabel(activityId);
        return new SpinnerValue(label, frame);
    }

    private static string DescribeToolCall((string Name, StringBuilder Arguments) toolCall)
    {
        var name = TerminalText.Sanitize(toolCall.Name);
        var arguments = TerminalText.Sanitize(toolCall.Arguments.ToString());
        return arguments.Length == 0 ? name : $"tool call {name}: {arguments}";
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

    private string CreateToolLabel(string activityId)
    {
        var toolCallId = activityId[ToolActivityPrefix.Length..];
        return $"{Name}: {_toolCalls[toolCallId].Name}";
    }
}

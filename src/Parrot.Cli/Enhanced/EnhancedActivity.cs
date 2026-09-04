using Parrot.Cli.Enhanced.Tools;
using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedActivity(ToolPresenterRegistry presenters)
{
    private const string Redacted = "<redacted>";

    private readonly Dictionary<(string AgentSessionId, string ToolCallId), string> _toolNames = [];

    public void Observe(Event published)
    {
        switch (published.PayloadCase)
        {
            case Event.PayloadOneofCase.TurnStarted:
                Remove(published.AgentSessionId);
                break;
            case Event.PayloadOneofCase.ToolCallChunk:
                Track(published.AgentSessionId, published.ToolCallChunk.ToolCallId, published.ToolCallChunk.ToolName);
                break;
            case Event.PayloadOneofCase.ToolStarted:
                Track(published.AgentSessionId, published.ToolStarted.ToolCallId, published.ToolStarted.ToolName);
                break;
            case Event.PayloadOneofCase.ToolFinished:
                Remove(published.AgentSessionId, published.ToolFinished.ToolCallId);
                break;
            case Event.PayloadOneofCase.ToolCancelled:
                Remove(published.AgentSessionId, published.ToolCancelled.ToolCallId);
                break;
            case Event.PayloadOneofCase.ToolError:
                Remove(published.AgentSessionId, published.ToolError.ToolCallId);
                break;
            case Event.PayloadOneofCase.TurnEnded:
            case Event.PayloadOneofCase.TurnFailed:
            case Event.PayloadOneofCase.AgentFinished:
            case Event.PayloadOneofCase.AgentFailed:
                Remove(published.AgentSessionId);
                break;
            default:
                break;
        }
    }

    public string Format(Event published, bool started) => published.PayloadCase switch
    {
        Event.PayloadOneofCase.None => $"event {TerminalText.Sanitize(published.Id)} has no payload",
        Event.PayloadOneofCase.TurnStarted =>
            $"turn started: {TerminalText.Sanitize(published.TurnStarted.Model)}",
        Event.PayloadOneofCase.InputAdmitted =>
            $"{(started ? "queued" : "input admitted")}: {TerminalText.Sanitize(published.InputAdmitted.Content)}",
        Event.PayloadOneofCase.InputPromoted =>
            $"input promoted: {TerminalText.Sanitize(published.InputPromoted.InputId)}",
        Event.PayloadOneofCase.QueueSnapshot => string.Empty,
        Event.PayloadOneofCase.ToolCallChunk => Format(published.AgentSessionId, published.ToolCallChunk),
        Event.PayloadOneofCase.RetryNotice =>
            $"retry {published.RetryNotice.Attempt} in {published.RetryNotice.RetryAfterMs} ms: " +
            TerminalText.Sanitize(published.RetryNotice.Reason),
        Event.PayloadOneofCase.ToolStarted =>
            $"* {TerminalText.Sanitize(published.ToolStarted.ToolName)} started",
        Event.PayloadOneofCase.ToolFinished =>
            $"+ {TerminalText.Sanitize(published.ToolFinished.ToolName)} finished",
        Event.PayloadOneofCase.ToolCancelled =>
            $"- {TerminalText.Sanitize(published.ToolCancelled.ToolName)} cancelled",
        Event.PayloadOneofCase.ToolError =>
            $"! {TerminalText.Sanitize(published.ToolError.ToolName)}: " +
            TerminalText.Sanitize(published.ToolError.Message),
        Event.PayloadOneofCase.AgentStarted =>
            $"* agent {TerminalText.Sanitize(published.AgentStarted.Name)} started",
        Event.PayloadOneofCase.AgentFinished =>
            $"+ agent {TerminalText.Sanitize(published.AgentFinished.Name)} finished " +
            $"({AgentDurationFormatter.Format(published.AgentFinished.ElapsedMs)})",
        Event.PayloadOneofCase.AgentFailed =>
            $"! agent {TerminalText.Sanitize(published.AgentFailed.Name)}: " +
            TerminalText.Sanitize(published.AgentFailed.Message),
        Event.PayloadOneofCase.CompactionStarted => "* compaction started",
        Event.PayloadOneofCase.CompactionFinished => "+ compaction finished",
        Event.PayloadOneofCase.CompactionFailed =>
            $"! compaction failed: {TerminalText.Sanitize(published.CompactionFailed.Message)}",
        _ => string.Empty,
    };

    private string Format(string agentSessionId, ToolCallChunk chunk)
    {
        var name = Resolve(agentSessionId, chunk.ToolCallId, chunk.ToolName);
        var fragment = name.Length == 0
            ? Redacted
            : TerminalText.Sanitize(ToolPresentationRedactor.Redact(
                new ToolCallPresentation(string.Empty, name, chunk.ArgumentsFragment),
                presenters.Describe(name)).ArgumentsJson);
        return name.Length == 0
            ? $"tool call: {fragment}"
            : $"tool call {TerminalText.Sanitize(name)}: {fragment}";
    }

    private string Resolve(string agentSessionId, string toolCallId, string toolName) =>
        toolName.Length > 0
            ? toolName
            : _toolNames.TryGetValue((agentSessionId, toolCallId), out var tracked)
                ? tracked
                : string.Empty;

    private void Track(string agentSessionId, string toolCallId, string toolName)
    {
        if (toolCallId.Length > 0 && toolName.Length > 0)
        {
            _toolNames[(agentSessionId, toolCallId)] = toolName;
        }
    }

    private void Remove(string agentSessionId, string toolCallId) =>
        _ = _toolNames.Remove((agentSessionId, toolCallId));

    private void Remove(string agentSessionId)
    {
        foreach (var key in _toolNames.Keys.Where(key =>
                     string.Equals(key.AgentSessionId, agentSessionId, StringComparison.Ordinal)).ToArray())
        {
            _ = _toolNames.Remove(key);
        }
    }
}

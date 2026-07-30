using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal static class EnhancedActivity
{
    internal static string Format(Event published, bool started) => published.PayloadCase switch
    {
        Event.PayloadOneofCase.None => $"event {TerminalText.Sanitize(published.Id)} has no payload",
        Event.PayloadOneofCase.TurnStarted =>
            $"turn started: {TerminalText.Sanitize(published.TurnStarted.Model)}",
        Event.PayloadOneofCase.InputAdmitted =>
            $"{(started ? "queued" : "input admitted")}: {TerminalText.Sanitize(published.InputAdmitted.Content)}",
        Event.PayloadOneofCase.InputPromoted =>
            $"input promoted: {TerminalText.Sanitize(published.InputPromoted.InputId)}",
        Event.PayloadOneofCase.QueueSnapshot => string.Empty,
        Event.PayloadOneofCase.ToolCallChunk =>
            $"tool call {TerminalText.Sanitize(published.ToolCallChunk.ToolName)}: " +
            TerminalText.Sanitize(published.ToolCallChunk.ArgumentsFragment),
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
            $"+ agent {TerminalText.Sanitize(published.AgentFinished.Name)} finished",
        Event.PayloadOneofCase.AgentFailed =>
            $"! agent {TerminalText.Sanitize(published.AgentFailed.Name)}: " +
            TerminalText.Sanitize(published.AgentFailed.Message),
        _ => string.Empty,
    };
}

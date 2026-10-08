using Parrot.Protocol;

namespace Parrot.Cli.Enhanced;

internal static class TerminalToolEvent
{
    public static (string ToolCallId, string ToolName) ReadTool(Event published) => published.PayloadCase switch
    {
        Event.PayloadOneofCase.ToolFinished => (published.ToolFinished.ToolCallId, published.ToolFinished.ToolName),
        Event.PayloadOneofCase.ToolCancelled => (published.ToolCancelled.ToolCallId, published.ToolCancelled.ToolName),
        Event.PayloadOneofCase.ToolError => (published.ToolError.ToolCallId, published.ToolError.ToolName),
        _ => (string.Empty, string.Empty),
    };
}

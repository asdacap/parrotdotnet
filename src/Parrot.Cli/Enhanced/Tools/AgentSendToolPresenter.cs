using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class AgentSendToolPresenter : IToolPresenter
{
    public string ToolName => "agent_send";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var sessionId = arguments.RootElement.GetProperty("session_id").GetString() ?? string.Empty;
        var message = arguments.RootElement.GetProperty("message").GetString() ?? string.Empty;
        return new ToolLiveValue($"{call.Owner}: Send to {sessionId}", [message], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var sessionId = arguments.RootElement.GetProperty("session_id").GetString() ?? string.Empty;
        return new ToolScrollbackValue(
            $"{call.Owner}: Send to {sessionId}",
            [.. Details(arguments.RootElement, terminal)],
            Status(terminal));
    }

    private static IEnumerable<string> Details(JsonElement arguments, ToolTerminalPresentation terminal)
    {
        yield return arguments.GetProperty("message").GetString() ?? string.Empty;

        if (terminal.ResultPresent)
        {
            yield return terminal.Result;
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }

    private static ToolTerminalStatus Status(ToolTerminalPresentation terminal) =>
        terminal.ResultPresent && terminal.Result.StartsWith("error: ", StringComparison.Ordinal)
            ? ToolTerminalStatus.ReportedFailure
            : terminal.Status;
}

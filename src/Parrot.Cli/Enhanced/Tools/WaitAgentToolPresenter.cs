using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WaitAgentToolPresenter : IToolPresenter
{
    public string ToolName => "wait_agent";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: Wait for {SessionId(arguments.RootElement)}", [], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        return new ToolScrollbackValue(
            $"{call.Owner}: Wait for {SessionId(arguments.RootElement)}",
            [.. Details(terminal)],
            Status(terminal));
    }

    private static string SessionId(JsonElement arguments) =>
        arguments.GetProperty("session_id").GetString() ?? string.Empty;

    private static IEnumerable<string> Details(ToolTerminalPresentation terminal)
    {
        if (terminal.ResultPresent)
        {
            yield return terminal.Result;
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }

    private static ToolTerminalStatus Status(ToolTerminalPresentation terminal)
    {
        if (!terminal.ResultPresent)
        {
            return terminal.Status;
        }

        if (terminal.Result.StartsWith("error: ", StringComparison.Ordinal))
        {
            return ToolTerminalStatus.ReportedFailure;
        }

        using var result = JsonDocument.Parse(terminal.Result);
        if (!result.RootElement.TryGetProperty("status", out var status)
            || status.ValueKind != JsonValueKind.String)
        {
            return terminal.Status;
        }

        return status.GetString() switch
        {
            "failed" => ToolTerminalStatus.ReportedFailure,
            "canceled" => ToolTerminalStatus.Cancelled,
            _ => terminal.Status,
        };
    }
}

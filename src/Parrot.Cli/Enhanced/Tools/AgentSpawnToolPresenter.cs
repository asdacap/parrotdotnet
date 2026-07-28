using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class AgentSpawnToolPresenter : IToolPresenter
{
    public string ToolName => "agent_spawn";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: Start agent", [.. Details(arguments.RootElement)], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        return new ToolScrollbackValue(
            $"{call.Owner}: Start agent",
            [.. TerminalDetails(arguments.RootElement, terminal)],
            Status(terminal));
    }

    private static IEnumerable<string> Details(JsonElement arguments)
    {
        if (arguments.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } value)
        {
            yield return value;
        }

        yield return arguments.GetProperty("prompt").GetString() ?? string.Empty;
    }

    private static IEnumerable<string> TerminalDetails(JsonElement arguments, ToolTerminalPresentation terminal)
    {
        foreach (var detail in Details(arguments))
        {
            yield return detail;
        }

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

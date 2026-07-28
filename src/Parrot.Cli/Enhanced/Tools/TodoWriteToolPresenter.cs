using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class TodoWriteToolPresenter : IToolPresenter
{
    public string ToolName => "todowrite";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: Update todo list", [.. Details(arguments.RootElement)], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var details = terminal.ResultPresent && !terminal.Result.StartsWith("error: ", StringComparison.Ordinal)
            ? ResultDetails(terminal.Result)
            : [.. FailureDetails(arguments.RootElement, terminal)];
        return new ToolScrollbackValue($"{call.Owner}: Update todo list", details, Status(terminal));
    }

    private static string[] ResultDetails(string result)
    {
        using var document = JsonDocument.Parse(result);
        return [.. Details(document.RootElement)];
    }

    private static IEnumerable<string> Details(JsonElement root)
    {
        var todos = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("todos");
        foreach (var todo in todos.EnumerateArray())
        {
            var status = todo.GetProperty("status").GetString() ?? string.Empty;
            var content = todo.GetProperty("content").GetString() ?? string.Empty;
            yield return $"[{status}] {content}";
        }
    }

    private static IEnumerable<string> FailureDetails(JsonElement arguments, ToolTerminalPresentation terminal)
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

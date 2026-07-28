using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class TodoReadToolPresenter : IToolPresenter
{
    public string ToolName => "todoread";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: Todo list", [], frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        new ToolScrollbackValue($"{call.Owner}: Todo list", [.. Details(terminal)], Status(terminal));

    private static IEnumerable<string> Details(ToolTerminalPresentation terminal)
    {
        if (terminal.ResultPresent && terminal.Result.StartsWith("error: ", StringComparison.Ordinal))
        {
            yield return terminal.Result;
        }
        else if (terminal.ResultPresent)
        {
            using var result = JsonDocument.Parse(terminal.Result);
            foreach (var todo in result.RootElement.EnumerateArray())
            {
                var status = todo.GetProperty("status").GetString() ?? string.Empty;
                var content = todo.GetProperty("content").GetString() ?? string.Empty;
                yield return $"[{status}] {content}";
            }
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

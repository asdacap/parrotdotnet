using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class TodoWriteToolPresenter : IToolPresenter
{
    public string ToolName => "todowrite";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var details = Details(arguments.RootElement).ToArray();
        return new ToolLiveValue(
            $"{call.Owner}: TODO · {details.Length} {(details.Length == 1 ? "item" : "items")}",
            ToolBlock.FromTodos(string.Join('\n', details)),
            Metadata,
            frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var inputDetails = Details(arguments.RootElement).ToArray();
        var details = status == ToolTerminalStatus.Succeeded
            ? ResultDetails(terminal.Result)
            : inputDetails;
        var block = status == ToolTerminalStatus.Succeeded
            ? ToolBlock.FromTodos(string.Join('\n', details))
            : terminal.DescribeBlock(ToolBlockKind.None);
        return new ToolScrollbackValue(
            $"{call.Owner}: TODO · {details.Length} {(details.Length == 1 ? "item" : "items")}",
            block,
            status,
            Metadata);
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
            yield return $"{Marker(status)} {content}";
        }
    }

    private static string Marker(string status) => status switch
    {
        "in_progress" => "◐",
        "completed" => "✓",
        "cancelled" => "■",
        _ => "○",
    };
}

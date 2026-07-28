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
        var count = Count(arguments.RootElement);
        return new ToolLiveValue(
            $"{call.Owner}: TODO · {count} {(count == 1 ? "item" : "items")}",
            ToolBlock.FromTodos(string.Join('\n', details)),
            Metadata,
            frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var inputDetails = Details(arguments.RootElement).ToArray();
        var inputCount = Count(arguments.RootElement);
        var details = status == ToolTerminalStatus.Succeeded
            ? ResultDetails(terminal.Result)
            : inputDetails;
        var count = status == ToolTerminalStatus.Succeeded
            ? ResultCount(terminal.Result)
            : inputCount;
        var block = status == ToolTerminalStatus.Succeeded
            ? ToolBlock.FromTodos(string.Join('\n', details))
            : terminal.DescribeBlock(ToolBlockKind.None);
        return new ToolScrollbackValue(
            $"{call.Owner}: TODO · {count} {(count == 1 ? "item" : "items")}",
            block,
            status,
            Metadata);
    }

    private static string[] ResultDetails(string result)
    {
        using var document = JsonDocument.Parse(result);
        return [.. Details(document.RootElement)];
    }

    private static int ResultCount(string result)
    {
        using var document = JsonDocument.Parse(result);
        return Count(document.RootElement);
    }

    private static int Count(JsonElement root) => Todos(root).GetArrayLength();

    private static IEnumerable<string> Details(JsonElement root)
    {
        var todos = Todos(root);
        if (todos.GetArrayLength() == 0)
        {
            yield return "No todos";
            yield break;
        }

        foreach (var todo in todos.EnumerateArray())
        {
            var content = todo.GetProperty("content").GetString();
            var status = todo.GetProperty("status").GetString();
            var priority = todo.GetProperty("priority").GetString();
            if (string.IsNullOrWhiteSpace(content)
                || status is not ("pending" or "in_progress" or "completed" or "cancelled")
                || priority is not ("high" or "medium" or "low"))
            {
                throw new FormatException("A todo must have content, status, and priority.");
            }

            yield return $"{Marker(status)} {priority} · {content}";
        }
    }

    private static JsonElement Todos(JsonElement root)
    {
        var todos = root.ValueKind == JsonValueKind.Array ? root : root.GetProperty("todos");
        return todos.ValueKind == JsonValueKind.Array
            ? todos
            : throw new FormatException("The todo list must be an array.");
    }

    private static string Marker(string status) => status switch
    {
        "pending" => "○",
        "in_progress" => "◐",
        "completed" => "✓",
        "cancelled" => "■",
        _ => throw new FormatException("The todo status is not supported."),
    };
}

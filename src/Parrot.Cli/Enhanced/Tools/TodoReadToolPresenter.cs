using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class TodoReadToolPresenter : IToolPresenter
{
    public string ToolName => "todoread";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: Todo list", [], Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        var block = status == ToolTerminalStatus.Succeeded
            ? ToolBlock.FromTodos(string.Join('\n', Details(terminal)))
            : terminal.DescribeBlock(ToolBlockKind.None);
        return new ToolScrollbackValue($"{call.Owner}: Todo list", block, status, Metadata);
    }

    private static IEnumerable<string> Details(ToolTerminalPresentation terminal)
    {
        using var result = JsonDocument.Parse(terminal.Result);
        foreach (var todo in result.RootElement.EnumerateArray())
        {
            var status = todo.GetProperty("status").GetString() ?? string.Empty;
            var content = todo.GetProperty("content").GetString() ?? string.Empty;
            yield return $"{Marker(status)} {content}";
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
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

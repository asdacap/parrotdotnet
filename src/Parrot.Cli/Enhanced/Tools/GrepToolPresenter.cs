using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class GrepToolPresenter : IToolPresenter
{
    public string ToolName => "grep";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var (pattern, path) = Arguments(call.ArgumentsJson);
        return new ToolLiveValue(Label(call.Owner, pattern, path), [], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var (pattern, path) = Arguments(call.ArgumentsJson);
        return new ToolScrollbackValue(Label(call.Owner, pattern, path), Details(terminal), Status(terminal));
    }

    private static (string Pattern, string Path) Arguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return (String(root, "pattern"), String(root, "path"));
    }

    private static string Label(string owner, string pattern, string path) => path.Length == 0
        ? $"{owner}: grep {pattern}"
        : $"{owner}: grep {pattern} in {path}";

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

    private static ToolTerminalStatus Status(ToolTerminalPresentation terminal) =>
        terminal.ResultPresent && terminal.Result.StartsWith("error: ", StringComparison.Ordinal)
            ? ToolTerminalStatus.ReportedFailure
            : terminal.Status;

    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class GlobToolPresenter : IToolPresenter
{
    public string ToolName => "glob";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        Style = ToolPresentationStyle.Muted,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var (pattern, path) = Arguments(call.ArgumentsJson);
        return new ToolLiveValue(Label(call.Owner, pattern, path), [], Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var (pattern, path) = Arguments(call.ArgumentsJson);
        return DescribeTerminal(call.Owner, pattern, path, terminal);
    }

    private static (string Pattern, string Path) Arguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return (String(root, "pattern"), String(root, "path"));
    }

    private static string Label(string owner, string pattern, string path) => path.Length == 0
        ? $"{owner}: glob \"{pattern}\""
        : $"{owner}: glob \"{pattern}\" in {path}";

    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private ToolScrollbackValue DescribeTerminal(
        string owner,
        string pattern,
        string path,
        ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        var label = Label(owner, pattern, path);
        if (status == ToolTerminalStatus.Succeeded)
        {
            var count = ToolOutputText.CountLines(terminal.Result);
            label += $" · {count} {(count == 1 ? "path" : "paths")}";
        }

        return new ToolScrollbackValue(
            label,
            status == ToolTerminalStatus.Succeeded ? ToolBlock.Empty : terminal.DescribeBlock(ToolBlockKind.None),
            status,
            Metadata);
    }
}

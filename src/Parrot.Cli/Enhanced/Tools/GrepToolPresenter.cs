using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class GrepToolPresenter : IToolPresenter
{
    public string ToolName => "grep";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        Style = ToolPresentationStyle.Muted,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var (pattern, path, include) = Arguments(call.ArgumentsJson);
        return new ToolLiveValue(Label(call.Owner, pattern, path, include), [], Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var (pattern, path, include) = Arguments(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var label = Label(call.Owner, pattern, path, include);
        if (status == ToolTerminalStatus.Succeeded)
        {
            var count = ToolOutputText.CountLines(terminal.Result);
            label += $" · {count} {(count == 1 ? "match" : "matches")}";
        }

        return new ToolScrollbackValue(
            label,
            status == ToolTerminalStatus.Succeeded ? ToolBlock.Empty : terminal.DescribeBlock(ToolBlockKind.None),
            status,
            Metadata);
    }

    private static (string Pattern, string Path, string Include) Arguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return (String(root, "pattern"), String(root, "path"), String(root, "include"));
    }

    private static string Label(string owner, string pattern, string path, string include)
    {
        var label = path.Length == 0
            ? $"{owner}: grep \"{pattern}\" in ."
            : $"{owner}: grep \"{pattern}\" in {path}";
        return include.Length == 0 ? label : $"{label} matching \"{include}\"";
    }

    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

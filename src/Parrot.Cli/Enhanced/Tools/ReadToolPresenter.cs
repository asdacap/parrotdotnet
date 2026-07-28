using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ReadToolPresenter : IToolPresenter
{
    public string ToolName => "read";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        Style = ToolPresentationStyle.Muted,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: read {Path(call.ArgumentsJson)}", [], Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var (path, offset) = Arguments(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var block = status == ToolTerminalStatus.Succeeded
            ? ToolBlock.FromCode(terminal.Result, Language(path), path, offset)
            : terminal.DescribeBlock(ToolBlockKind.None);
        return new ToolScrollbackValue($"{call.Owner}: read {path}", block, status, Metadata);
    }

    private static string Path(string argumentsJson) => Arguments(argumentsJson).Path;

    private static (string Path, int Offset) Arguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        var path = root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("path", out var pathValue)
            && pathValue.ValueKind == JsonValueKind.String
            ? pathValue.GetString() ?? string.Empty
            : string.Empty;
        var offset = root.TryGetProperty("offset", out var offsetValue)
            && offsetValue.TryGetInt32(out var value)
            ? value
            : 1;
        return (path, offset);
    }

    private static string Language(string path) => System.IO.Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".cs" => "csharp",
        ".go" => "go",
        ".js" => "javascript",
        ".json" => "json",
        ".md" => "markdown",
        ".py" => "python",
        ".rs" => "rust",
        ".sh" => "shell",
        ".ts" => "typescript",
        ".yaml" or ".yml" => "yaml",
        _ => string.Empty,
    };
}

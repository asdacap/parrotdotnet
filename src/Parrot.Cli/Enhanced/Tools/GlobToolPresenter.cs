using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class GlobToolPresenter : IToolPresenter
{
    public string ToolName => "glob";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        Style = ToolPresentationStyle.Muted,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: glob \"{Pattern(call.ArgumentsJson)}\"", [], Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        DescribeTerminal(call.Owner, Pattern(call.ArgumentsJson), terminal);

    private static string Pattern(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("pattern", out var pattern)
            && pattern.ValueKind == JsonValueKind.String
            ? pattern.GetString() ?? string.Empty
            : string.Empty;
    }

    private ToolScrollbackValue DescribeTerminal(
        string owner,
        string pattern,
        ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        var label = $"{owner}: glob \"{pattern}\"";
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

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
        var path = Path(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var block = status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
            ? ToolBlock.FromError(
                $"{ToolYamlFormatter.Format(call.ArgumentsJson)}\n---\n{terminal.DescribeBlock(ToolBlockKind.None).Text}")
            : ToolBlock.Empty;
        return new ToolScrollbackValue($"{call.Owner}: read {path}", block, status, Metadata);
    }

    private static string Path(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("path", out var pathValue)
            && pathValue.ValueKind == JsonValueKind.String
            ? pathValue.GetString() ?? string.Empty
            : string.Empty;
    }
}

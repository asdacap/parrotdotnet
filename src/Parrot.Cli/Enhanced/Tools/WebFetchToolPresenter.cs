using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WebFetchToolPresenter : IToolPresenter
{
    public string ToolName => "web_fetch";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        Style = ToolPresentationStyle.Muted,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"web fetch {Request(call.ArgumentsJson)}", [], Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        new ToolScrollbackValue(
            $"web fetch {Request(call.ArgumentsJson)}",
            terminal.DescribeBlock(ToolBlockKind.Text),
            terminal.ResolveStatus(),
            Metadata);

    private static string Request(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("url", out var url)
            || url.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var target = url.GetString() ?? string.Empty;
        var method = root.TryGetProperty("method", out var methodValue)
            && methodValue.ValueKind == JsonValueKind.String
            ? methodValue.GetString()?.ToUpperInvariant() ?? "GET"
            : "GET";
        return $"{method} {target}";
    }
}

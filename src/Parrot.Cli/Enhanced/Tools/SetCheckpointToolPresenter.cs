using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class SetCheckpointToolPresenter : IToolPresenter
{
    public string ToolName => "set_checkpoint";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue(Label(Title(call.ArgumentsJson)), ToolBlock.Empty, Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        return new ToolScrollbackValue(
            Label(Title(call.ArgumentsJson)),
            status == ToolTerminalStatus.Succeeded ? ToolBlock.Empty : terminal.DescribeBlock(ToolBlockKind.None),
            status,
            Metadata);
    }

    private static string Label(string title) => $"Set checkpoint {title}";

    private static string Title(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("title", out var title)
            || title.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("set_checkpoint requires a string title.");
        }

        return title.GetString() ?? string.Empty;
    }
}

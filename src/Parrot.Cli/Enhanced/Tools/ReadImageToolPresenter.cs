using System.Globalization;
using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ReadImageToolPresenter : IToolPresenter
{
    public string ToolName => "read_image";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        Style = ToolPresentationStyle.Muted,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"read image {Path(call.ArgumentsJson)}", [], Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var label = $"read image {Path(call.ArgumentsJson)}";
        var status = terminal.ResolveStatus();
        if (status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure)
        {
            return new ToolScrollbackValue(label, terminal.DescribeBlock(ToolBlockKind.None), status, Metadata);
        }

        if (terminal.Artifacts is [var artifact, ..])
        {
            label += $" ({FormatBytes(artifact.ByteLength)}, {artifact.Width}x{artifact.Height})";
        }

        return new ToolScrollbackValue(label, ToolBlock.Empty, status, Metadata);
    }

    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0.0} KB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / (1024.0 * 1024):0.0} MB"),
    };

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

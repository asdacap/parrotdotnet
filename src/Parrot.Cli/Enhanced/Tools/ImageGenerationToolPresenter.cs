using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ImageGenerationToolPresenter : IToolPresenter
{
    public string ToolName => "imagegen";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        Style = ToolPresentationStyle.Muted,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue("running imagegen", [], Metadata, frame);

    public IScrollbackItem PresentStarted(ToolCallPresentation call)
    {
        var (outputPath, details) = Read(call.ArgumentsJson);
        return new ToolScrollbackValue(
            $"imagegen {outputPath}",
            details,
            ToolTerminalStatus.Succeeded,
            Metadata with { SuccessIcon = TerminalIcons.Pending });
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var (outputPath, details) = Read(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        var error = status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
            ? terminal.DescribeBlock(ToolBlockKind.None).Text
            : string.Empty;
        return new ToolScrollbackValue(
            $"imagegen {outputPath}",
            error.Length == 0 ? details : [.. details, error],
            status,
            Metadata);
    }

    private static (string OutputPath, string[] Details) Read(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        var outputPath = root.TryGetProperty("output_path", out var outputPathValue)
            ? outputPathValue.GetString() ?? string.Empty
            : string.Empty;
        var prompt = root.TryGetProperty("prompt", out var promptValue)
            ? promptValue.GetString() ?? string.Empty
            : string.Empty;
        var references = root.TryGetProperty("referenced_image_paths", out var referencesValue)
            ? referencesValue.EnumerateArray().Select(static reference => reference.GetString()).ToArray()
            : [];
        return (outputPath, references.Length == 0 ? [prompt] : [prompt, $"references: {string.Join(", ", references)}"]);
    }
}

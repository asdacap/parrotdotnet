using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class EditToolPresenter : IToolPresenter
{
    private const string NoChanges = "No changes made.";

    public string ToolName => "edit";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue(Label(Path(call.ArgumentsJson)), ToolBlock.Empty, Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        return new ToolScrollbackValue(
            Label(Path(call.ArgumentsJson)),
            DescribeBlock(terminal, status),
            status,
            Metadata);
    }

    private static ToolBlock DescribeBlock(ToolTerminalPresentation terminal, ToolTerminalStatus status)
    {
        if (status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure)
        {
            return terminal.DescribeBlock(ToolBlockKind.None);
        }

        if (!terminal.ResultPresent || terminal.Result.Length == 0
            || string.Equals(terminal.Result, NoChanges, StringComparison.Ordinal))
        {
            return ToolBlock.Empty;
        }

        return LooksLikeUnifiedDiff(terminal.Result)
            ? ToolBlock.FromDiff(terminal.Result)
            : ToolBlock.FromText(terminal.Result);
    }

    private static bool LooksLikeUnifiedDiff(string result)
    {
        var firstLineEnd = result.IndexOf('\n');
        if (firstLineEnd < 0 || !result.AsSpan(0, firstLineEnd).StartsWith("--- ", StringComparison.Ordinal))
        {
            return false;
        }

        return result.AsSpan(firstLineEnd + 1).StartsWith("+++ ", StringComparison.Ordinal);
    }

    private static string Label(string path) => $"edit {path}";

    private static string Path(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("path", out var path)
            && path.ValueKind == JsonValueKind.String
                ? path.GetString() ?? string.Empty
                : string.Empty;
    }
}

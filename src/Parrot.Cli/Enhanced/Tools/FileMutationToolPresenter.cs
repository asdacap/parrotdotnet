using System.Text.Json;
using Parrot.Files;
using Parrot.Llm;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class FileMutationToolPresenter(string toolName) : IToolPresenter
{
    public string ToolName => toolName;

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue(Label(Path(call.ArgumentsJson)), ToolBlock.Empty, Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        var path = Path(call.ArgumentsJson);
        var block = DescribeBlock(terminal, status);
        return block.Kind == ToolBlockKind.Diff
            ? new DiffToolScrollbackValue(ToolName, path, block.Text)
            : new ToolScrollbackValue(Label(path), block, status, Metadata);
    }

    private static ToolBlock DescribeBlock(ToolTerminalPresentation terminal, ToolTerminalStatus status)
    {
        if (status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure)
        {
            return terminal.DescribeBlock(ToolBlockKind.None);
        }

        if (!terminal.ResultPresent || terminal.Result.Length == 0
            || string.Equals(terminal.Result, FileMutation.NoChanges, StringComparison.Ordinal))
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

    private static string Path(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object ? JsonRead.String(root, "path") : string.Empty;
    }

    private string Label(string path) => $"{toolName} {path}";
}

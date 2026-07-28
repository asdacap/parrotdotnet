using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class GitDiffToolPresenter : IToolPresenter
{
    public string ToolName => "git_diff";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var (target, reference) = Arguments(call.ArgumentsJson);
        return new ToolLiveValue(Label(call.Owner, target, reference), [], Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var (target, reference) = Arguments(call.ArgumentsJson);
        var status = terminal.ResolveStatus();
        return new ToolScrollbackValue(
            CompletedLabel(call.Owner, target, reference, terminal, status),
            terminal.DescribeBlock(ToolBlockKind.Diff),
            status,
            Metadata);
    }

    private static (string Target, string Reference) Arguments(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        var target = String(root, "target");
        return (target.Length == 0 ? "uncommitted" : target, String(root, "ref"));
    }

    private static string Label(string owner, string target, string reference) => reference.Length == 0
        ? $"{owner}: git diff {target}"
        : $"{owner}: git diff {target} {reference}";

    private static string CompletedLabel(
        string owner,
        string target,
        string reference,
        ToolTerminalPresentation terminal,
        ToolTerminalStatus status) => status == ToolTerminalStatus.Succeeded
            ? $"{Label(owner, target, reference)} · {ToolOutputText.CountLines(terminal.Result)} lines"
            : Label(owner, target, reference);

    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

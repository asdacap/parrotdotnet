using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class GitDiffToolPresenter : IToolPresenter
{
    public string ToolName => "git_diff";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var (target, reference) = Arguments(call.ArgumentsJson);
        return new ToolLiveValue(Label(call.Owner, target, reference), [], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var (target, reference) = Arguments(call.ArgumentsJson);
        return new ToolScrollbackValue(
            Label(call.Owner, target, reference),
            Details(terminal),
            Status(terminal));
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

    private static IEnumerable<string> Details(ToolTerminalPresentation terminal)
    {
        if (terminal.ResultPresent)
        {
            yield return terminal.Result;
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }

    private static ToolTerminalStatus Status(ToolTerminalPresentation terminal) =>
        terminal.ResultPresent && terminal.Result.StartsWith("error: ", StringComparison.Ordinal)
            ? ToolTerminalStatus.ReportedFailure
            : terminal.Status;

    private static string String(JsonElement root, string name) => root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}

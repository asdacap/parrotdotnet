using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ReadToolPresenter : IToolPresenter
{
    public string ToolName => "read";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: read {Path(call.ArgumentsJson)}", [], frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        new ToolScrollbackValue(
            $"{call.Owner}: read {Path(call.ArgumentsJson)}",
            Details(terminal),
            Status(terminal));

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
}

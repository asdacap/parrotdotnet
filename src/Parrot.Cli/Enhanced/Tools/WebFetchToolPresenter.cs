using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WebFetchToolPresenter : IToolPresenter
{
    public string ToolName => "web_fetch";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: web fetch {Host(call.ArgumentsJson)}", [], frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        new ToolScrollbackValue(
            $"{call.Owner}: web fetch {Host(call.ArgumentsJson)}",
            Details(terminal),
            Status(terminal));

    private static string Host(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;

        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("url", out var url)
            || url.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        var value = url.GetString() ?? string.Empty;
        return Uri.TryCreate(value, UriKind.Absolute, out var address) && address.Host.Length > 0
            ? address.Host
            : value;
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

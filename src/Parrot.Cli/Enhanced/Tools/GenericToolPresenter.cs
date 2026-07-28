namespace Parrot.Cli.Enhanced.Tools;

internal sealed class GenericToolPresenter : IToolPresenter
{
    public string ToolName => "generic";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: {call.ToolName}", Detail(call.ArgumentsJson), frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var suffix = terminal.Status == ToolTerminalStatus.Cancelled ? " cancelled" : string.Empty;
        return new ToolScrollbackValue(
            $"{call.Owner}: tool call {call.ToolName}{suffix}",
            TerminalDetails(call, terminal),
            Status(terminal));
    }

    private static IEnumerable<string> Detail(string value) => value.Length == 0 ? [] : [value];

    private static ToolTerminalStatus Status(ToolTerminalPresentation terminal) =>
        terminal.ResultPresent && terminal.Result.StartsWith("error: ", StringComparison.Ordinal)
            ? ToolTerminalStatus.ReportedFailure
            : terminal.Status;

    private static IEnumerable<string> TerminalDetails(
        ToolCallPresentation call,
        ToolTerminalPresentation terminal)
    {
        if (call.ArgumentsJson.Length > 0)
        {
            yield return call.ArgumentsJson;
        }

        if (terminal.ResultPresent)
        {
            yield return terminal.Result;
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }
}

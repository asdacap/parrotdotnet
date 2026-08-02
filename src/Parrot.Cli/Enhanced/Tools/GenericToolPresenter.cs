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
            terminal.ResolveStatus());
    }

    private static IEnumerable<string> Detail(string value) =>
        value.Length == 0 ? [] : [ToolYamlFormatter.Format(value)];

    private static IEnumerable<string> TerminalDetails(
        ToolCallPresentation call,
        ToolTerminalPresentation terminal)
    {
        if (call.ArgumentsJson.Length > 0)
        {
            yield return ToolYamlFormatter.Format(call.ArgumentsJson);
        }

        if (terminal.ResultPresent)
        {
            var separator = call.ArgumentsJson.Length > 0 ? "---\n" : string.Empty;
            yield return $"{separator}{ToolYamlFormatter.Format(terminal.Result)}";
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }
}

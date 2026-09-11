namespace Parrot.Cli.Enhanced.Tools;

internal sealed class StatusToolPresenter : IToolPresenter
{
    public string ToolName => "status";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: status", [], frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        var block = status == ToolTerminalStatus.Succeeded && terminal.ResultPresent
            ? ToolBlock.FromStatus(terminal.Result)
            : terminal.DescribeBlock(ToolBlockKind.Text);
        return new ToolScrollbackValue(
            $"{call.Owner}: tool call status", block, status, ToolPresentationMetadata.Default);
    }
}

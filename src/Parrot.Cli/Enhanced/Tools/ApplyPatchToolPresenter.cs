namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ApplyPatchToolPresenter : IToolPresenter
{
    public string ToolName => "apply_patch";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: apply patch", ToolBlock.Empty, Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        return new ToolScrollbackValue(
            $"{call.Owner}: apply patch",
            terminal.DescribeBlock(ToolBlockKind.Diff),
            status,
            Metadata);
    }
}

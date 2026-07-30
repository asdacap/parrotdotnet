namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WaitToolPresenter : IToolPresenter
{
    public string ToolName => "wait";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        LiveOnly = true,
        Modeline = true,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: Wait for incoming activity", [], Metadata, frame);

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) => null;
}

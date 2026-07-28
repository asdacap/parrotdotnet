namespace Parrot.Cli.Enhanced.Tools;

internal interface IToolPresenter
{
    string ToolName { get; }

    ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    ILiveBufferItem PresentLive(ToolCallPresentation call, int frame);

    IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal);
}

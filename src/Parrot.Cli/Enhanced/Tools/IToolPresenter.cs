namespace Parrot.Cli.Enhanced.Tools;

internal interface IToolPresenter
{
    string ToolName { get; }

    ILiveBufferItem PresentLive(ToolCallPresentation call, int frame);

    IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal);
}

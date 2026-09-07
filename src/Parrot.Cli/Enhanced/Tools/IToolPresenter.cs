namespace Parrot.Cli.Enhanced.Tools;

/// <summary>Presents a tool call as live or committed terminal content.</summary>
internal interface IToolPresenter
{
    string ToolName { get; }

    ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    ILiveBufferItem PresentLive(ToolCallPresentation call, int frame);

    /// <summary>Returns the terminal presentation, or null when the tool should leave no scrollback item.</summary>
    IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal);
}

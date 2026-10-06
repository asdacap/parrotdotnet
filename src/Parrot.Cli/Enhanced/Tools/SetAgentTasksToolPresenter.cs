namespace Parrot.Cli.Enhanced.Tools;

internal sealed class SetAgentTasksToolPresenter(IToolPresenter generic) : IToolPresenter
{
    public string ToolName => "set_agent_tasks";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default with
    {
        RedactedInputFields = ["path", "tasks"],
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue("setting agent tasks", [], frame);

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        generic.PresentTerminal(call with { ArgumentsJson = string.Empty }, terminal);
}

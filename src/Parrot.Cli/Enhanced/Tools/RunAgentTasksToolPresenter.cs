namespace Parrot.Cli.Enhanced.Tools;

internal sealed class RunAgentTasksToolPresenter(IToolPresenter generic) : IToolPresenter
{
    public string ToolName => "run_agent_tasks";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default with
    {
        RedactedInputFields = ["path", "artifact"],
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue("running agent tasks", [], frame);

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        generic.PresentTerminal(call with { ArgumentsJson = string.Empty }, terminal);
}

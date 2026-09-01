namespace Parrot.Cli.Enhanced.Tools;

internal sealed class RunAgentTasksToolPresenter : IToolPresenter
{
    private readonly GenericToolPresenter _generic = new();

    public string ToolName => "run_agent_tasks";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default with
    {
        RedactedInputFields = ["path", "artifact"],
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue($"{call.Owner}: running agent tasks", [], frame);

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) =>
        _generic.PresentTerminal(call with { ArgumentsJson = string.Empty }, terminal);
}

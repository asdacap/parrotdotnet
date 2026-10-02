using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class AgentStatusToolPresenter : IToolPresenter
{
    public string ToolName => "agent_status";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame) =>
        new ToolLiveValue(Label(call), ToolBlock.Empty, Metadata, frame);

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var status = terminal.ResolveStatus();
        var block = status == ToolTerminalStatus.Succeeded && terminal.ResultPresent
            ? ToolBlock.FromStatus(terminal.Result)
            : terminal.DescribeBlock(ToolBlockKind.Text);
        return new ToolScrollbackValue(Label(call), block, status, Metadata);
    }

    private static string Label(ToolCallPresentation call)
    {
        using var document = JsonDocument.Parse(call.ArgumentsJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("agent_status requires a string name.");
        }

        return $"Agent status · {call.ResolveAgentReference(name.GetString() ?? string.Empty)}";
    }
}

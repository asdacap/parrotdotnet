using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class AgentSendToolPresenter : IToolPresenter
{
    public string ToolName => "agent_send";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var sessionId = arguments.RootElement.GetProperty("session_id").GetString() ?? string.Empty;
        return new ToolLiveValue($"{call.Owner}: Send to {sessionId}", [], Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var sessionId = arguments.RootElement.GetProperty("session_id").GetString() ?? string.Empty;
        var message = arguments.RootElement.GetProperty("message").GetString() ?? string.Empty;
        var status = terminal.ResolveStatus();
        return new ToolScrollbackValue(
            $"{call.Owner}: Send to {sessionId}",
            status == ToolTerminalStatus.Succeeded ? ToolBlock.FromText(message) : terminal.DescribeBlock(ToolBlockKind.None),
            status,
            Metadata);
    }
}

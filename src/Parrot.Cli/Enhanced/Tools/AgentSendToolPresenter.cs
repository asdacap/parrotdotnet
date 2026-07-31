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
        var recipient = call.ResolveAgentReference(sessionId);
        return new ToolLiveValue($"{call.Owner}: Send to {recipient}", [], Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        var sessionId = arguments.RootElement.GetProperty("session_id").GetString() ?? string.Empty;
        var message = arguments.RootElement.GetProperty("message").GetString() ?? string.Empty;
        var status = terminal.ResolveStatus();
        var recipient = status == ToolTerminalStatus.Succeeded
            ? ReadRecipientName(terminal.Result) ?? call.ResolveAgentReference(sessionId)
            : call.ResolveAgentReference(sessionId);
        return new ToolScrollbackValue(
            $"{call.Owner}: Send to {recipient}",
            status == ToolTerminalStatus.Succeeded ? ToolBlock.FromText(message) : terminal.DescribeBlock(ToolBlockKind.None),
            status,
            Metadata);
    }

    private static string? ReadRecipientName(string result)
    {
        try
        {
            using var document = JsonDocument.Parse(result);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String
                && name.GetString() is { Length: > 0 } value
                    ? value
                    : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

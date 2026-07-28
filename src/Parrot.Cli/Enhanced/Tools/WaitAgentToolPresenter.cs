using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WaitAgentToolPresenter : IToolPresenter
{
    public string ToolName => "wait_agent";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        LiveOnly = true,
        Modeline = true,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        using var arguments = JsonDocument.Parse(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: Wait for {SessionId(arguments.RootElement)}", [], Metadata, frame);
    }

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) => null;

    private static string SessionId(JsonElement arguments) =>
        arguments.GetProperty("session_id").GetString() ?? string.Empty;
}

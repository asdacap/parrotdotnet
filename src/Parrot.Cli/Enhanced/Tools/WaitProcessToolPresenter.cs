using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WaitProcessToolPresenter : IToolPresenter
{
    public string ToolName => "wait_process";

    public ToolPresentationMetadata Metadata { get; } = ToolPresentationMetadata.Default with
    {
        LiveOnly = true,
        Modeline = true,
    };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var name = Process(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: wait {name}", [], Metadata, frame);
    }

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal) => null;

    private static string Process(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? string.Empty
            : throw new FormatException("wait_process requires a string name.");
    }
}

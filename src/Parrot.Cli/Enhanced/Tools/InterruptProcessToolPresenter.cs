using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class InterruptProcessToolPresenter : IToolPresenter
{
    public string ToolName => "interrupt_process";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var name = Process(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: interrupt {name}", [], Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var name = Process(call.ArgumentsJson);
        var status = terminal.ResolveProcessStatus();
        var block = status == ToolTerminalStatus.Succeeded
            ? ToolBlock.Empty
            : ToolBlock.FromError(terminal.ResultPresent ? terminal.Result : terminal.Error);
        return new ToolScrollbackValue($"{call.Owner}: interrupt {name}", block, status, Metadata);
    }

    private static string Process(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? string.Empty
            : throw new FormatException("interrupt_process requires a string name.");
    }
}

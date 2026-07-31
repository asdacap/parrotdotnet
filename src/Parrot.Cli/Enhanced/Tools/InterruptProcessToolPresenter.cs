using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class InterruptProcessToolPresenter : IToolPresenter
{
    public string ToolName => "interrupt_process";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var label = Process(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: {label}", [], Metadata, frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var label = Process(call.ArgumentsJson);
        var status = terminal.ResolveProcessStatus();
        var block = status == ToolTerminalStatus.Succeeded
            ? ToolBlock.Empty
            : ToolBlock.FromError(terminal.ResultPresent ? terminal.Result : terminal.Error);
        return new ToolScrollbackValue($"{call.Owner}: {label}", block, status, Metadata);
    }

    private static string Process(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("name", out var name)
            || name.ValueKind != JsonValueKind.String)
        {
            throw new FormatException("interrupt_process requires a string name.");
        }

        var signal = 2;
        if (root.TryGetProperty("signal", out var signalElement)
            && signalElement.ValueKind != JsonValueKind.Null
            && (signalElement.ValueKind != JsonValueKind.Number || !signalElement.TryGetInt32(out signal)))
        {
            throw new FormatException("interrupt_process signal must be an integer.");
        }

        return $"signal {signal} {name.GetString() ?? string.Empty}";
    }
}

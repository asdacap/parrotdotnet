using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class InterruptProcessToolPresenter : IToolPresenter
{
    public string ToolName => "interrupt_process";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var name = Process(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: interrupt {name}", [], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var name = Process(call.ArgumentsJson);
        return new ToolScrollbackValue(
            $"{call.Owner}: interrupt {name}",
            Details(terminal),
            terminal.ResolveProcessStatus());
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

    private static IEnumerable<string> Details(ToolTerminalPresentation terminal)
    {
        if (terminal.ResultPresent)
        {
            yield return terminal.Result;
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }
}

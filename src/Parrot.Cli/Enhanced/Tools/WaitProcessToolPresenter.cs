using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class WaitProcessToolPresenter : IToolPresenter
{
    public string ToolName => "wait_process";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var name = Process(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: wait {name}", [], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var name = Process(call.ArgumentsJson);
        var yielded = terminal.ResultPresent && terminal.Result == name;
        var label = yielded
            ? $"{call.Owner}: {name} still running"
            : $"{call.Owner}: wait {name}";
        return new ToolScrollbackValue(label, Details(terminal, yielded), terminal.ResolveProcessStatus());
    }

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

    private static IEnumerable<string> Details(ToolTerminalPresentation terminal, bool yielded)
    {
        if (terminal.ResultPresent && !yielded)
        {
            yield return terminal.Result;
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }
}

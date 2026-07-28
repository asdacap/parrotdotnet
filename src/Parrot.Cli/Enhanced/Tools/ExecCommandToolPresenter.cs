using System.Globalization;
using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ExecCommandToolPresenter : IToolPresenter
{
    public string ToolName => "exec_command";

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var command = Command(call.ArgumentsJson);
        return new ToolLiveValue($"{call.Owner}: $ {command}", [command], frame);
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var command = Command(call.ArgumentsJson);
        var requestedName = Name(call.ArgumentsJson);
        var yielded = terminal.ResultPresent && IsYielded(terminal.Result, requestedName);
        var processName = yielded ? terminal.Result : requestedName;
        var label = yielded
            ? $"{call.Owner}: $ {command} (process {processName} running)"
            : $"{call.Owner}: $ {command}";
        return new ToolScrollbackValue(label, Details(command, terminal, yielded), terminal.ResolveProcessStatus());
    }

    private static string Command(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("command", out var command)
            && command.ValueKind == JsonValueKind.String
            ? command.GetString() ?? string.Empty
            : throw new FormatException("exec_command requires a string command.");
    }

    private static string Name(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        return document.RootElement.TryGetProperty("name", out var name)
            && name.ValueKind == JsonValueKind.String
            ? name.GetString() ?? string.Empty
            : string.Empty;
    }

    private static IEnumerable<string> Details(string command, ToolTerminalPresentation terminal, bool yielded)
    {
        yield return command;
        if (terminal.ResultPresent && !yielded)
        {
            yield return terminal.Result;
        }

        if (terminal.Error.Length > 0)
        {
            yield return terminal.Error;
        }
    }

    private static bool IsYielded(string result, string requestedName) =>
        requestedName.Length > 0
            ? string.Equals(result, requestedName, StringComparison.Ordinal)
            : result.StartsWith("shell-", StringComparison.Ordinal)
              && int.TryParse(result.AsSpan("shell-".Length), NumberStyles.None, CultureInfo.InvariantCulture, out _);
}

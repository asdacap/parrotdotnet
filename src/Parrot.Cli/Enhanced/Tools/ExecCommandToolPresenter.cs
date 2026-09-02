using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ExecCommandToolPresenter(
    TimeProvider timeProvider,
    IReadOnlyList<string> readOnlyCommandPrefixes) : IToolPresenter
{
    public string ToolName => "exec_command";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var command = Command(call.ArgumentsJson);
        var isReadOnly = IsReadOnlyCommand(command);
        var metadata = MetadataFor(isReadOnly);
        return isReadOnly
            ? new ToolLiveValue($"{call.Owner}: $ {command}", ToolBlock.Empty, metadata, frame)
            : new ToolLiveValue(
                $"{call.Owner}: $ {command}",
                [],
                metadata,
                frame,
                new RunningDuration(timeProvider));
    }

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        if (terminal.YieldedProcess is not null)
        {
            return null;
        }

        var command = Command(call.ArgumentsJson);
        var isReadOnly = IsReadOnlyCommand(command);
        var label = $"{call.Owner}: $ {command}";
        var status = terminal.ResolveProcessStatus();
        var block = status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
            ? ToolBlock.FromOutput(ToolOutputText.Tail(terminal.ResultPresent ? terminal.Result : terminal.Error, 10))
            : isReadOnly && status == ToolTerminalStatus.Succeeded
                ? ToolBlock.Empty
                : ToolBlock.FromOutput(ToolOutputText.Tail(terminal.Result, 10));
        return new ToolScrollbackValue(label, block, status, MetadataFor(isReadOnly));
    }

    private static bool IsShellWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';

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

    private ToolPresentationMetadata MetadataFor(bool isReadOnly) =>
        isReadOnly ? Metadata with { Style = ToolPresentationStyle.Muted } : Metadata;

    private bool IsReadOnlyCommand(string command)
    {
        var firstCommandCharacter = 0;
        while (firstCommandCharacter < command.Length && IsShellWhitespace(command[firstCommandCharacter]))
        {
            firstCommandCharacter++;
        }

        var commandStart = command[firstCommandCharacter..];
        return readOnlyCommandPrefixes.Any(prefix =>
            commandStart.StartsWith(prefix, StringComparison.Ordinal) &&
            (commandStart.Length == prefix.Length || IsShellWhitespace(commandStart[prefix.Length])));
    }
}

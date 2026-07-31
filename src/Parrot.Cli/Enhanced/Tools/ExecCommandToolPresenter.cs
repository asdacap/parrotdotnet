using System.Text.Json;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ExecCommandToolPresenter(TimeProvider timeProvider) : IToolPresenter
{
    public ExecCommandToolPresenter()
        : this(TimeProvider.System)
    {
    }

    public string ToolName => "exec_command";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default;

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var command = Command(call.ArgumentsJson);
        return new ToolLiveValue(
            $"{call.Owner}: $ {command}",
            [],
            Metadata,
            frame,
            new RunningDuration(timeProvider));
    }

    public IScrollbackItem PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        var command = Command(call.ArgumentsJson);
        var yielded = terminal.YieldedProcess;
        var label = yielded is null
            ? $"{call.Owner}: $ {command}"
            : $"{call.Owner}: $ {command} (process {yielded.Name} running)";
        var status = terminal.ResolveProcessStatus();
        var block = yielded is not null
            ? ToolBlock.Empty
            : status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
                ? ToolBlock.FromOutput(ToolOutputText.Tail(terminal.ResultPresent ? terminal.Result : terminal.Error, 10))
                : ToolBlock.FromOutput(ToolOutputText.Tail(terminal.Result, 10));
        return new ToolScrollbackValue(label, block, status, Metadata);
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
}

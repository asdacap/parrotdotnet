using System.Text.Json;
using Parrot.Tools;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ExecCommandToolPresenter(
    TimeProvider timeProvider,
    IReadOnlyList<string> readOnlyCommandPrefixes) : IToolPresenter
{
    private readonly ReadOnlyExecCommandClassifier _readOnlyCommandClassifier = new(readOnlyCommandPrefixes);

    public string ToolName => "exec_command";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default with { MultilineLabel = true };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var command = Command(call.ArgumentsJson);
        var isReadOnly = IsReadOnlyCommand(command);
        var metadata = MetadataFor(isReadOnly);
        return isReadOnly
            ? new ToolLiveValue($"$ {command}", ToolBlock.Empty, metadata, frame)
            : new ToolLiveValue(
                $"$ {command}",
                [],
                metadata,
                frame,
                RunningTimer(new RunningDuration(timeProvider)));
    }

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        if (terminal.YieldedProcess is not null)
        {
            return null;
        }

        var command = Command(call.ArgumentsJson);
        var isReadOnly = IsReadOnlyCommand(command);
        var label = $"$ {command}";
        var status = terminal.ResolveProcessStatus();
        var block = status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
            ? ToolBlock.FromOutput(ToolOutputText.Tail(terminal.ResultPresent ? WithoutLoneStdoutLabel(terminal.Result) : terminal.Error, 10))
            : isReadOnly && status == ToolTerminalStatus.Succeeded
                ? ToolBlock.Empty
                : ToolBlock.FromOutput(ToolOutputText.Tail(WithoutLoneStdoutLabel(terminal.Result), 10));
        return new ToolScrollbackValue(label, block, status, MetadataFor(isReadOnly));
    }

    private static Func<string> RunningTimer(RunningDuration runningDuration) =>
        () => $"running {runningDuration.Format()}";

    private static string Command(string argumentsJson)
    {
        using var document = JsonDocument.Parse(argumentsJson);
        var root = document.RootElement;
        return root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("command", out var command)
            && command.ValueKind == JsonValueKind.String
                ? command.GetString() ?? string.Empty
                : root.TryGetProperty("cmd", out var aliased)
                    && aliased.ValueKind == JsonValueKind.String
                        ? aliased.GetString() ?? string.Empty
                        : throw new FormatException("exec_command requires a string command.");
    }

    private static string WithoutLoneStdoutLabel(string result)
    {
        const string StdoutLabel = "\n[stdout]\n";
        var labelIndex = result.IndexOf(StdoutLabel, StringComparison.Ordinal);
        return labelIndex < 0 || result.Contains("\n[stderr]\n", StringComparison.Ordinal)
            ? result
            : result.Remove(labelIndex + 1, StdoutLabel.Length - 1);
    }

    private ToolPresentationMetadata MetadataFor(bool isReadOnly) =>
        isReadOnly ? Metadata with { Style = ToolPresentationStyle.Muted } : Metadata;

    private bool IsReadOnlyCommand(string command) => _readOnlyCommandClassifier.IsMatch(command);
}

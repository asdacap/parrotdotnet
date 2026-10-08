using System.Text.Json;
using Parrot.Llm;
using Parrot.Tools;

namespace Parrot.Cli.Enhanced.Tools;

internal sealed class ExecCommandToolPresenter(
    TimeProvider timeProvider,
    IReadOnlyList<string> readOnlyCommandPrefixes) : IToolPresenter
{
    private readonly ReadOnlyExecCommandClassifier _readOnlyCommandClassifier = new(readOnlyCommandPrefixes);

    public string ToolName => "exec_command";

    public ToolPresentationMetadata Metadata => ToolPresentationMetadata.Default with { MultilineLabel = true, DeferStarted = true };

    public ILiveBufferItem PresentLive(ToolCallPresentation call, int frame)
    {
        var arguments = ExecCommandArguments.Parse(call.ArgumentsJson);
        var isReadOnly = IsReadOnlyCommand(arguments.Command);
        var metadata = MetadataFor(isReadOnly);
        return isReadOnly
            ? new ToolLiveValue(arguments.Label, ToolBlock.Empty, metadata, frame)
            : new ToolLiveValue(
                arguments.Label,
                [],
                metadata,
                frame,
                RunningTimer(new RunningDuration(timeProvider)));
    }

    public IScrollbackItem? PresentStarted(ToolCallPresentation call)
    {
        var arguments = ExecCommandArguments.Parse(call.ArgumentsJson);
        return IsNamedLongCommand(arguments.Name, arguments.Description, arguments.Command)
            ? new ToolScrollbackValue(
                arguments.LabelWithCommand,
                ToolBlock.Empty,
                ToolTerminalStatus.Succeeded,
                MetadataFor(IsReadOnlyCommand(arguments.Command)) with { SuccessIcon = TerminalIcons.Pending })
            : null;
    }

    public IScrollbackItem? PresentTerminal(ToolCallPresentation call, ToolTerminalPresentation terminal)
    {
        if (terminal.YieldedProcess is not null)
        {
            return null;
        }

        var arguments = ExecCommandArguments.Parse(call.ArgumentsJson);
        var isReadOnly = IsReadOnlyCommand(arguments.Command);
        var label = terminal.StartOmitted ? arguments.LabelWithCommand : arguments.Label;
        var status = terminal.ResolveProcessStatus();
        var block = status is ToolTerminalStatus.Errored or ToolTerminalStatus.ReportedFailure
            ? ToolBlock.FromOutput(ToolOutputText.Tail(terminal.ResultPresent ? WithoutLoneStdoutLabel(terminal.Result) : terminal.Error, 10))
            : isReadOnly && status == ToolTerminalStatus.Succeeded
                ? ToolBlock.Empty
                : ToolBlock.FromOutput(ToolOutputText.Tail(WithoutLoneStdoutLabel(terminal.Result), 10));
        return new ToolScrollbackValue(label, block, status, MetadataFor(isReadOnly));
    }

    // A long command with a name and description, usually an inline script, is committed in full when it
    // starts, so later presentations, including its yielded process, show only its name and description.
    internal static string Summarize(string? name, string? description, string command) =>
        IsNamedLongCommand(name, description, command) ? $"{name} · {description}" : command;

    private static Func<string> RunningTimer(RunningDuration runningDuration) =>
        () => $"running {runningDuration.Format()}";

    private static bool IsNamedLongCommand(string? name, string? description, string command) =>
        command.Length > ExecCommandTool.LongCommandLength
        && name is not null
        && !string.IsNullOrWhiteSpace(description);

    private static string WithoutLoneStdoutLabel(string result)
    {
        const string stdoutLabel = "\n[stdout]\n";
        var labelIndex = result.IndexOf(stdoutLabel, StringComparison.Ordinal);
        return labelIndex < 0 || result.Contains("\n[stderr]\n", StringComparison.Ordinal)
            ? result
            : result.Remove(labelIndex + 1, stdoutLabel.Length - 1);
    }

    private ToolPresentationMetadata MetadataFor(bool isReadOnly) =>
        isReadOnly ? Metadata with { Style = ToolPresentationStyle.Muted } : Metadata;

    private bool IsReadOnlyCommand(string command) => _readOnlyCommandClassifier.IsMatch(command);

    private readonly record struct ExecCommandArguments(string Command, string? Name, string? Description)
    {
        public string Label => $"$ {Summarize(Name, Description, Command)}";

        public string LabelWithCommand => $"{Label}\n{Command}";

        public static ExecCommandArguments Parse(string argumentsJson)
        {
            using var document = JsonDocument.Parse(argumentsJson);
            var root = document.RootElement;
            return new(
                root.ValueKind == JsonValueKind.Object && JsonRead.TryReadString(root, "command", out var command)
                    ? command
                    : JsonRead.TryReadString(root, "cmd", out var aliased)
                        ? aliased
                        : throw new FormatException("exec_command requires a string command."),
                OptionalString(root, "name"),
                OptionalString(root, "description"));
        }

        private static string? OptionalString(JsonElement root, string propertyName) =>
            JsonRead.TryReadString(root, propertyName, out var value) ? value : null;
    }
}

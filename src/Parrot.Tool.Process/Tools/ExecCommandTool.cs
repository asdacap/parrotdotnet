using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Process;

namespace Parrot.Tools;

// The one tool that reaches outside the process. It runs under the sandbox, so
// a failure to sandbox is reported to the model rather than run unconfined --
// the fail-closed property, surfaced as a tool error the model can react to.
internal sealed class ExecCommandTool(
    IProcessOwner processes,
    IAgentSession session,
    ToolWorkspace workspace,
    ReadOnlyExecCommandClassifier readOnlyCommandClassifier,
    IPromptTemplateCatalog templates) : ITool
{
    internal const int LongCommandLength = 200;
    internal const int NamedCommandLength = 300;

    private readonly ReadOnlyExecCommandClassifier _readOnlyCommandClassifier = readOnlyCommandClassifier;

    public string Name => "exec_command";

    public bool IsParallelSafe(ToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        try
        {
            var input = Input.Parse(invocation.ArgumentsJson);
            if (input.Name is { } name && name.Trim().Length == 0)
            {
                return false;
            }

            return input.Command.Length > 0 && _readOnlyCommandClassifier.IsMatch(input.Command);
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return false;
        }
    }

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string command;
        ProcessEnvironmentOverrides environment;
        string? name;
        string? description;
        TimeSpan? yieldAfter;
        ShellProcessTerminalMode terminalMode;

        try
        {
            (command, environment, name, description, yieldAfter, terminalMode) = Input.Parse(invocation.ArgumentsJson);
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        if (command.Length == 0)
        {
            return ToolResultFormatter.Error(invocation, "no command given");
        }

        if (name is not null)
        {
            name = name.Trim();

            if (name.Length == 0)
            {
                return ToolResultFormatter.Error(invocation, "process name must not be empty");
            }
        }

        if (TryRedundantChangeDirectory(command, out var target))
        {
            return ToolResultFormatter.Error(
                invocation,
                $"current directory is already '{target}'; do not change directory to conserve token.");
        }

        if (TryRejectSleep(command, out var sleepMessage))
        {
            return ToolResultFormatter.Error(invocation, sleepMessage);
        }

        try
        {
            var process = processes.Start(
                name,
                command,
                description ?? string.Empty,
                invocation.CallId,
                environment,
                session,
                selection.SecurityProfile,
                terminalMode,
                new ShellProcessCompletionReport());
            var outcome = await process.Wait(yieldAfter, cancellationToken).ConfigureAwait(false);

            var text = outcome.Format();
            if ((command.Trim().Contains('\n', StringComparison.Ordinal) || command.Length > NamedCommandLength)
                && (name is null || string.IsNullOrWhiteSpace(description)))
            {
                text += "\n\n" + templates.Render("exec-command.metadata-reminder", [
                    new PromptTemplateArgument("command_length", NamedCommandLength.ToString(CultureInfo.InvariantCulture)),
                ]);
            }

            return new ToolExecutionResult(text, outcome.YieldedProcess);
        }
        catch (Exception failure) when (
            failure is SandboxUnavailableException or InvalidOperationException or IOException or PlatformNotSupportedException)
        {
            // Deliberate containment: fail closed, and tell the model why rather
            // than run the command outside the sandbox.
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    private static bool IsShellWhitespace(char character) => character is ' ' or '\t' or '\r' or '\n';

    private static bool IsSleepBoundary(char character) =>
        IsShellWhitespace(character) || character is ';' or '&';

    private static int FindCommandTerminator(string command, int start)
    {
        for (var index = start; index < command.Length; index++)
        {
            if (command[index] is ';' or '\n' or '\r' or '&')
            {
                return index;
            }
        }

        return command.Length;
    }

    private static bool TryRejectSleep(string command, out string message)
    {
        message = string.Empty;
        var start = 0;
        while (start < command.Length && IsShellWhitespace(command[start]))
        {
            start++;
        }

        if (!command.AsSpan(start).StartsWith("sleep", StringComparison.Ordinal))
        {
            return false;
        }

        var boundary = start + 5;
        if (boundary < command.Length && !IsSleepBoundary(command[boundary]))
        {
            return false;
        }

        message = "leading 'sleep' is not allowed in exec_command; use the wait tool to pause instead.";
        return true;
    }

    private bool TryRedundantChangeDirectory(string command, out string target)
    {
        target = string.Empty;
        var start = 0;
        while (start < command.Length && IsShellWhitespace(command[start]))
        {
            start++;
        }

        if (start + 2 >= command.Length
            || command[start] != 'c'
            || command[start + 1] != 'd'
            || !IsShellWhitespace(command[start + 2]))
        {
            return false;
        }

        start += 3;
        var end = FindCommandTerminator(command, start);
        target = command[start..end].Trim();
        if (target.Length > 1 && target.StartsWith('"') && target.EndsWith('"'))
        {
            target = target[1..^1];
        }

        if (target.Length == 0)
        {
            return false;
        }

        return workspace.ResolvesToRoot(ExpandWorkDirectory(target));
    }

    private string ExpandWorkDirectory(string target) => target switch
    {
        "." or "$WORKDIR" or "${WORKDIR}" => workspace.Root,
        _ when target.StartsWith("$WORKDIR/", StringComparison.Ordinal)
            || target.StartsWith("${WORKDIR}/", StringComparison.Ordinal) =>
            target.Replace("$WORKDIR/", workspace.Root + "/", StringComparison.Ordinal)
                .Replace("${WORKDIR}/", workspace.Root + "/", StringComparison.Ordinal),
        _ => target,
    };

    internal sealed class Input
    {
        [JsonPropertyName("command")]
        public string? Command { get; init; }

        // Duplicate of Command as an undocumented alias: some weaker models emit
        // 'cmd' instead of the schema's 'command', so both are accepted.
        [JsonPropertyName("cmd")]
        public string? Cmd { get; init; }

        [JsonPropertyName("env")]
        [JsonConverter(typeof(ProcessEnvironmentJsonConverter))]
        public Dictionary<string, string>? Environment { get; init; }

        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("description")]
        public string? Description { get; init; }

        [JsonPropertyName("yield_after_ms")]
        public long? YieldAfterMilliseconds { get; init; }

        [JsonPropertyName("tty")]
        public bool Terminal { get; init; }

        public static (string Command, ProcessEnvironmentOverrides Environment, string? Name, string? Description, TimeSpan? YieldAfter, ShellProcessTerminalMode TerminalMode) Parse(string json)
        {
            ToolInputConversion.RequireObject(json, "command");
            var input = ToolInputConversion.Deserialize(json, ExecCommandToolJsonContext.Default.ExecCommandToolInput);
            return (
                input.Command ?? input.Cmd ?? throw new FormatException("Tool arguments require a string 'command'."),
                input.Environment is null ? ProcessEnvironmentOverrides.Empty : new ProcessEnvironmentOverrides(input.Environment),
                input.Name,
                input.Description,
                ToolInputConversion.ConvertDelay(input.YieldAfterMilliseconds, "yield_after_ms"),
                input.Terminal ? ShellProcessTerminalMode.PseudoTerminal : ShellProcessTerminalMode.Pipe);
        }
    }
}

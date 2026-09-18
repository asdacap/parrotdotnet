using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

// The one tool that reaches outside the process. It runs under the sandbox, so
// a failure to sandbox is reported to the model rather than run unconfined --
// the fail-closed property, surfaced as a tool error the model can react to.
internal sealed class ExecCommandTool(
    IProcessOwner processes,
    IAgentSession session,
    ToolWorkspace workspace,
    ReadOnlyExecCommandClassifier readOnlyCommandClassifier) : ITool
{
    private readonly ReadOnlyExecCommandClassifier _readOnlyCommandClassifier = readOnlyCommandClassifier;

    public string Name => "exec_command";

    public bool IsParallelSafe(ToolInvocation invocation)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        try
        {
            ToolInputConversion.RequireObject(invocation.ArgumentsJson, "command");
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, ExecCommandToolJsonContext.Default.ExecCommandToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var command = input.Command ?? input.Cmd
                ?? throw new FormatException("Tool arguments require a string 'command'.");
            _ = input.Environment is null
                ? ProcessEnvironmentOverrides.Empty
                : new ProcessEnvironmentOverrides(input.Environment);
            _ = ToolInputConversion.ConvertDelay(input.YieldAfterMilliseconds, "yield_after_ms");
            if (input.Name is { } name && name.Trim().Length == 0)
            {
                return false;
            }

            return command.Length > 0 && _readOnlyCommandClassifier.IsMatch(command);
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
        var argumentsJson = invocation.ArgumentsJson;
        string command;
        ProcessEnvironmentOverrides environment;
        string? name;
        TimeSpan? yieldAfter;
        ShellProcessTerminalMode terminalMode;

        try
        {
            ToolInputConversion.RequireObject(argumentsJson, "command");
            var input = JsonSerializer.Deserialize(argumentsJson, ExecCommandToolJsonContext.Default.ExecCommandToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            command = input.Command ?? input.Cmd
                ?? throw new FormatException("Tool arguments require a string 'command'.");
            environment = input.Environment is null
                ? ProcessEnvironmentOverrides.Empty
                : new ProcessEnvironmentOverrides(input.Environment);
            name = input.Name;
            yieldAfter = ToolInputConversion.ConvertDelay(input.YieldAfterMilliseconds, "yield_after_ms");
            terminalMode = input.Terminal ? ShellProcessTerminalMode.PseudoTerminal : ShellProcessTerminalMode.Pipe;
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

        try
        {
            var process = processes.Start(
                name,
                command,
                invocation.CallId,
                environment,
                session,
                selection.SecurityProfile,
                terminalMode);
            var outcome = await process.Wait(yieldAfter, cancellationToken).ConfigureAwait(false);

            return new ToolExecutionResult(outcome.Format(), outcome.YieldedProcess);
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

        [JsonPropertyName("yield_after_ms")]
        public long? YieldAfterMilliseconds { get; init; }

        [JsonPropertyName("tty")]
        public bool Terminal { get; init; }
    }
}

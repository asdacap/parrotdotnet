using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

// The one tool that reaches outside the process. It runs under the sandbox, so
// a failure to sandbox is reported to the model rather than run unconfined --
// the fail-closed property, surfaced as a tool error the model can react to.
internal sealed class ExecCommandTool(
    ShellProcessOwner processes,
    AgentSession session) : ITool
{
    public string Name => "exec_command";

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
            var input = JsonSerializer.Deserialize(argumentsJson, AgentProcessToolJsonContext.Default.ExecCommandToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            command = input.Command ?? throw new FormatException("Tool arguments require a string 'command'.");
            environment = input.Environment is null
                ? ProcessEnvironmentOverrides.Empty
                : new ProcessEnvironmentOverrides(input.Environment);
            name = input.Name;
            yieldAfter = ToolInputConversion.ConvertDelay(input.YieldAfterMilliseconds, "yield_after_ms");
            terminalMode = input.Terminal ? ShellProcessTerminalMode.PseudoTerminal : ShellProcessTerminalMode.Pipe;
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        if (command.Length == 0)
        {
            return "error: no command given";
        }

        if (name is not null)
        {
            name = name.Trim();

            if (name.Length == 0)
            {
                return "error: process name must not be empty";
            }
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
            return $"error: {failure.Message}";
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("command")]
        public string? Command { get; init; }

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

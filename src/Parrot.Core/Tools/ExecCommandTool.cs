using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;
using Parrot.Security;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

// The one tool that reaches outside the process. It runs under the sandbox, so
// a failure to sandbox is reported to the model rather than run unconfined --
// the fail-closed property, surfaced as a tool error the model can react to.
internal sealed partial class ExecCommandTool(
    ShellProcessOwner processes,
    AgentSession session,
    SecurityProfile securityProfile,
    Permissions.SandboxWriteGrants writeGrants) : ITool
{
    public string Name => "exec_command";

    public string Description =>
        "Run a sandboxed shell command. Optionally reserve a name unique among this agent session's running processes and yield "
        + "without stopping it; completed process names can be reused and omitted names are generated. Completion after a yield "
        + "is steered back to this agent unless wait_process claims it.";

    public string ParametersJson => Input.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
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
                environment,
                session,
                securityProfile,
                writeGrants.Capture(),
                terminalMode);
            var outcome = await process.Wait(yieldAfter, cancellationToken).ConfigureAwait(false);

            return outcome.Format();
        }
        catch (Exception failure) when (failure is SandboxUnavailableException or InvalidOperationException)
        {
            // Deliberate containment: fail closed, and tell the model why rather
            // than run the command outside the sandbox.
            return $"error: {failure.Message}";
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [Description("The shell command to run")]
        [JsonPropertyName("command")]
        [ToolRequired]
        public string? Command { get; init; }

        [Description("Environment variables for the command. Values override the inherited environment.")]
        [JsonPropertyName("env")]
        [JsonConverter(typeof(ProcessEnvironmentJsonConverter))]
        public Dictionary<string, string>? Environment { get; init; }

        [Description("Name unique among running processes; completed names can be reused and omitted names are generated")]
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [Description("Return the process name if still running after this many milliseconds")]
        [JsonPropertyName("yield_after_ms")]
        [ToolMinimum(0)]
        public long? YieldAfterMilliseconds { get; init; }

        [Description("Run the command in a pseudo-terminal so its standard input can accept later write_stdin calls")]
        [JsonPropertyName("tty")]
        [ToolDefaultBool(false)]
        public bool Terminal { get; init; }
    }
}

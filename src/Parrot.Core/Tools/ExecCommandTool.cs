using System.Text.Json;
using Parrot.Agent;
using Parrot.Process;
using Parrot.Security;

namespace Parrot.Tools;

// The one tool that reaches outside the process. It runs under the sandbox, so
// a failure to sandbox is reported to the model rather than run unconfined --
// the fail-closed property, surfaced as a tool error the model can react to.
internal sealed class ExecCommandTool(
    ShellProcessOwner processes,
    AgentSession session,
    SecurityProfile securityProfile) : ITool
{
    public string Name => "exec_command";

    public string Description =>
        "Run a sandboxed shell command. Optionally reserve a name unique among this agent session's running processes and yield "
        + "without stopping it; completed process names can be reused and omitted names are generated. Completion after a yield "
        + "is steered back to this agent unless wait_process claims it.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"command":{"type":"string","description":"The shell command to run"},"env":{"type":"object","additionalProperties":{"type":"string"},"description":"Environment variables for the command. Values override the inherited environment."},"name":{"type":"string","description":"Name unique among running processes; completed names can be reused and omitted names are generated"},"yield_after_ms":{"type":"integer","minimum":0,"description":"Return the process name if still running after this many milliseconds"}},"required":["command"],"additionalProperties":false}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string command;
        ProcessEnvironmentOverrides environment;
        string? name;
        TimeSpan? yieldAfter;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            command = arguments.RequiredString("command");
            environment = arguments.OptionalEnvironment("env");
            name = arguments.OptionalStrictString("name");
            yieldAfter = arguments.OptionalDelay("yield_after_ms");
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
            var process = processes.Start(name, command, environment, session, securityProfile);
            var outcome = await process.Wait(yieldAfter, cancellationToken).ConfigureAwait(false);

            return outcome.Yielded
                ? outcome.Name
                : ProcessResultFormatter.Format(outcome.Result ?? throw new InvalidOperationException("Missing result."));
        }
        catch (Exception failure) when (failure is SandboxUnavailableException or InvalidOperationException)
        {
            // Deliberate containment: fail closed, and tell the model why rather
            // than run the command outside the sandbox.
            return $"error: {failure.Message}";
        }
    }
}

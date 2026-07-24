using System.Text.Json;
using Parrot.Process;

namespace Parrot.Tools;

// The one tool that reaches outside the process. It runs under the sandbox, so
// a failure to sandbox is reported to the model rather than run unconfined --
// the fail-closed property, surfaced as a tool error the model can react to.
internal sealed class ExecCommandTool(
    ShellProcessOwner processes,
    Parrot.Agent.AgentSession session) : ITool
{
    public string Name => "exec_command";

    public string Description =>
        "Run a sandboxed shell command. Optionally reserve a unique session name and yield without stopping it; "
        + "omitted names are generated. Completion after a yield is steered back to this agent unless wait_process claims it.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"command":{"type":"string","description":"The shell command to run"},"name":{"type":"string","description":"Unique name within this user session; generated when omitted"},"yield_after_ms":{"type":"integer","minimum":0,"description":"Return the process name if still running after this many milliseconds"}},"required":["command"]}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string command;
        string? name;
        TimeSpan? yieldAfter;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            command = arguments.RequiredString("command");
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
            var process = processes.Start(name, command, session);
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

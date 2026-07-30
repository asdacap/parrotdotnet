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
    SecurityProfile securityProfile,
    Permissions.SandboxWriteGrants writeGrants) : ITool
{
    public string Name => "exec_command";

    public string Description =>
        "Run a sandboxed shell command. Optionally reserve a name unique among this agent session's running processes and yield "
        + "without stopping it; completed process names can be reused and omitted names are generated. Completion after a yield "
        + "is steered back to this agent unless wait_process claims it.";

    public string ParametersJson => ExecCommandToolInput.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string command;
        ProcessEnvironmentOverrides environment;
        string? name;
        TimeSpan? yieldAfter;

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
                writeGrants.Capture());
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

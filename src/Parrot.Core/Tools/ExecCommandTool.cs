using System.Text.Json;
using Parrot.Process;

namespace Parrot.Tools;

// The one tool that reaches outside the process. It runs under the sandbox, so
// a failure to sandbox is reported to the model rather than run unconfined --
// the fail-closed property, surfaced as a tool error the model can react to.
internal sealed class ExecCommandTool(
    string workingDirectory,
    string blobDirectory,
    ProcessRunner processes) : ITool
{
    public string Name => "exec_command";

    public string Description =>
        "Run a shell command. The host filesystem is read-only; the working directory is writable.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"command":{"type":"string","description":"The shell command to run"}},"required":["command"]}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string command;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            command = arguments.RequiredString("command");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return $"error: {failure.Message}";
        }

        if (command.Length == 0)
        {
            return "error: no command given";
        }

        try
        {
            var result = await processes
                .Run(command, workingDirectory, blobDirectory, cancellationToken)
                .ConfigureAwait(false);

            return Format(result);
        }
        catch (SandboxUnavailableException failure)
        {
            // Deliberate containment: fail closed, and tell the model why rather
            // than run the command outside the sandbox.
            return $"error: {failure.Message}";
        }
    }

    private static string Format(ProcessResult result)
    {
        if (result.Spilled)
        {
            return result.BlobPath;
        }

        var text = new System.Text.StringBuilder();
        _ = text.Append("Process exited with code ").Append(result.ExitCode);
        AppendOutput(text, "stdout", result.Stdout);
        AppendOutput(text, "stderr", result.Stderr);
        return text.ToString();
    }

    private static void AppendOutput(System.Text.StringBuilder text, string name, string output)
    {
        if (output.Length == 0)
        {
            return;
        }

        _ = text.Append('\n').Append('[').Append(name).Append("]\n").Append(output);
    }
}

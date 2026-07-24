using System.Text.Json;
using Parrot.Process;

namespace Parrot.Tools;

// The one tool that reaches outside the process. It runs under the sandbox, so
// a failure to sandbox is reported to the model rather than run unconfined --
// the fail-closed property, surfaced as a tool error the model can react to.
internal sealed class ExecCommandTool(string workingDirectory, ProcessRunner processes) : ITool
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
        var command = ReadString(argumentsJson, "command");

        if (command.Length == 0)
        {
            return "error: no command given";
        }

        try
        {
            var result = await processes
                .Run(command, workingDirectory, cancellationToken)
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
        var text = new System.Text.StringBuilder();
        _ = text.Append("exit ").Append(result.ExitCode).Append('\n');

        if (result.Stdout.Length > 0)
        {
            _ = text.Append(result.Stdout);
        }

        if (result.Stderr.Length > 0)
        {
            _ = text.Append("\n[stderr]\n").Append(result.Stderr);
        }

        return text.ToString();
    }

    private static string ReadString(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.TryGetProperty(property, out var value)
                && value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? string.Empty
                : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }
}

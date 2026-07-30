using System.Text.Json;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class WaitProcessTool(ShellProcessOwner processes) : ITool
{
    public string Name => "wait_process";

    public string Description =>
        "Wait for a shell process owned by this agent session. A timeout returns its name without stopping it, allowing a later wait.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"name":{"type":"string","description":"Reserved shell process name"},"yield_after_ms":{"type":"integer","minimum":0,"description":"Return the process name if still running after this many milliseconds"}},"required":["name"]}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string name;
        TimeSpan? yieldAfter;

        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            name = arguments.RequiredString("name").Trim();
            yieldAfter = arguments.OptionalDelay("yield_after_ms");

            if (name.Length == 0)
            {
                return "error: process name must not be empty";
            }

            var process = processes.Claim(name);
            var outcome = await process.Wait(yieldAfter, cancellationToken).ConfigureAwait(false);
            return outcome.Yielded
                ? outcome.Name
                : ProcessResultFormatter.Format(outcome.Result ?? throw new InvalidOperationException("Missing result."));
        }
        catch (Exception failure) when (failure is JsonException or FormatException or InvalidOperationException)
        {
            return $"error: {failure.Message}";
        }
    }
}

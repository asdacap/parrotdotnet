using System.Text.Json;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class InterruptProcessTool(ShellProcessOwner processes) : ITool
{
    public string Name => "interrupt_process";

    public string Description => "Interrupt a named running shell process and its process tree.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"name":{"type":"string","description":"Reserved shell process name"}},"required":["name"]}
        """;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            using var arguments = new ToolArguments(argumentsJson);
            var name = arguments.RequiredString("name").Trim();

            if (name.Length == 0)
            {
                return "error: process name must not be empty";
            }

            var process = processes.Claim(name);
            var result = await process.Interrupt(cancellationToken).ConfigureAwait(false);
            return result is null
                ? $"Shell process '{name}' interrupted."
                : ProcessResultFormatter.Format(result);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or InvalidOperationException)
        {
            return $"error: {failure.Message}";
        }
    }
}

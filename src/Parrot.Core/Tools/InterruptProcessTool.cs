using System.Text.Json;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class InterruptProcessTool(ShellProcessOwner processes) : ITool
{
    public string Name => "interrupt_process";

    public string Description => "Interrupt a running shell process owned by this agent session and its process tree.";

    public string ParametersJson => InterruptProcessToolInput.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(argumentsJson, OmittedAgentProcessToolJsonContext.Default.InterruptProcessToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var name = (input.Name ?? throw new FormatException("Tool arguments require a string 'name'.")).Trim();

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

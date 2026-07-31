using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Process;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class InterruptProcessTool(ShellProcessOwner processes) : ITool
{
    public string Name => "interrupt_process";

    public string Description => "Interrupt a running shell process owned by this agent session and its process tree.";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, OmittedAgentProcessToolJsonContext.Default.InterruptProcessToolInput)
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

    [ToolInputModel(AdditionalPropertiesPolicy.Omitted)]
    internal sealed partial class Input
    {
        [Description("Reserved shell process name")]
        [JsonPropertyName("name")]
        [ToolRequired]
        public string? Name { get; init; }
    }
}

using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Process;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class WaitProcessTool(ShellProcessOwner processes) : ITool
{
    public string Name => "wait_process";

    public string Description =>
        "Wait for a shell process owned by this agent session. A timeout returns its name without stopping it, allowing a later wait.";

    public string ParametersJson => Input.Descriptor;

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        string name;
        TimeSpan? yieldAfter;

        try
        {
            var input = JsonSerializer.Deserialize(argumentsJson, OmittedAgentProcessToolJsonContext.Default.WaitProcessToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            name = (input.Name ?? throw new FormatException("Tool arguments require a string 'name'.")).Trim();
            yieldAfter = ToolInputConversion.ConvertDelay(input.YieldAfterMilliseconds, "yield_after_ms");

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

    [ToolInputModel(AdditionalPropertiesPolicy.Omitted)]
    internal sealed partial class Input
    {
        [Description("Reserved shell process name")]
        [JsonPropertyName("name")]
        [ToolRequired]
        public string? Name { get; init; }

        [Description("Return the process name if still running after this many milliseconds")]
        [JsonPropertyName("yield_after_ms")]
        [ToolMinimum(0)]
        public long? YieldAfterMilliseconds { get; init; }
    }
}

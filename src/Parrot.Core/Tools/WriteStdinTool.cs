using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class WriteStdinTool(ShellProcessOwner processes) : ITool
{
    public string Name => "write_stdin";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            ToolInputConversion.RequireObject(invocation.ArgumentsJson, "name");
            var input = JsonSerializer.Deserialize(
                invocation.ArgumentsJson,
                OmittedAgentProcessToolJsonContext.Default.WriteStdinToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var name = (input.Name ?? throw new FormatException("Tool arguments require a string 'name'.")).Trim();
            var text = input.Text ?? throw new FormatException("Tool arguments require a string 'input'.");
            var yieldAfter = ToolInputConversion.ConvertDelay(input.YieldAfterMilliseconds, "yield_after_ms")
                ?? TimeSpan.FromMilliseconds(250);

            if (name.Length == 0)
            {
                return "error: process name must not be empty";
            }

            var result = await processes.WriteStdin(name, text, yieldAfter, cancellationToken).ConfigureAwait(false);
            return result.Format();
        }
        catch (Exception failure) when (failure is JsonException or FormatException or InvalidOperationException)
        {
            return $"error: {failure.Message}";
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("name")]
        [ToolRequired]
        public string? Name { get; init; }

        [JsonPropertyName("input")]
        [ToolRequired]
        public string? Text { get; init; }

        [JsonPropertyName("yield_after_ms")]
        [ToolMinimum(0)]
        [ToolDefaultLong(250)]
        public long? YieldAfterMilliseconds { get; init; }
    }
}

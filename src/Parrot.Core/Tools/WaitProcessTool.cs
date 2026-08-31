using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class WaitProcessTool(ShellProcessOwner processes) : ITool
{
    public string Name => "wait_process";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        var argumentsJson = invocation.ArgumentsJson;
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
            return new ToolExecutionResult(outcome.Format(), outcome.YieldedProcess);
        }
        catch (Exception failure) when (failure is JsonException or FormatException or InvalidOperationException)
        {
            return $"error: {failure.Message}";
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("yield_after_ms")]
        public long? YieldAfterMilliseconds { get; init; }
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class InterruptProcessTool(ShellProcessOwner processes) : ITool
{
    public string Name => "interrupt_process";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            ToolInputConversion.RequireObject(invocation.ArgumentsJson, "name");
            using var document = JsonDocument.Parse(invocation.ArgumentsJson);
            if (document.RootElement.TryGetProperty("signal", out var signalElement)
                && signalElement.ValueKind != JsonValueKind.Null
                && (signalElement.ValueKind != JsonValueKind.Number || !signalElement.TryGetInt32(out _)))
            {
                throw new FormatException("Tool argument 'signal' must be an integer.");
            }

            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, OmittedAgentProcessToolJsonContext.Default.InterruptProcessToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            var name = (input.Name ?? throw new FormatException("Tool arguments require a string 'name'.")).Trim();
            var signalValue = input.Signal ?? 2;

            if (signalValue is < 1 or > 64)
            {
                return "error: Tool argument 'signal' must be between 1 and 64.";
            }

            var signal = new ProcessSignal(signalValue);

            if (name.Length == 0)
            {
                return "error: process name must not be empty";
            }

            var process = processes.Claim(name);
            await process.SendSignal(signal, cancellationToken).ConfigureAwait(false);
            return $"Signal {signal.Value} sent to shell process '{name}'.";
        }
        catch (Exception failure) when (
            failure is JsonException or FormatException or IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return $"error: {failure.Message}";
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Omitted)]
    internal sealed partial class Input
    {
        [JsonPropertyName("name")]
        [ToolRequired]
        public string? Name { get; init; }

        [JsonPropertyName("signal")]
        [ToolDefaultLong(2)]
        [ToolMinimum(1)]
        [ToolMaximum(64)]
        public int? Signal { get; init; }
    }
}

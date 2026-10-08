using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class InterruptProcessTool(IProcessOwner processes, IAgentResolver resolver) : ITool
{
    public string Name => "interrupt_process";

    public bool IsEnabledAfterInterruption => true;

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

            var input = ToolInputConversion.Deserialize(invocation.ArgumentsJson, OmittedAgentProcessToolJsonContext.Default.InterruptProcessToolInput);
            var name = (input.Name ?? throw new FormatException("Tool arguments require a string 'name'.")).Trim();
            var signalValue = input.Signal ?? 2;

            if (signalValue is < 1 or > 64)
            {
                return ToolResultFormatter.Error(invocation, "Tool argument 'signal' must be between 1 and 64.");
            }

            var signal = new ProcessSignal(signalValue);

            if (name.Length == 0)
            {
                return ToolResultFormatter.Error(invocation, "process name must not be empty");
            }

            var owner = processes;
            if (name.Contains('/', StringComparison.Ordinal))
            {
                var resource = resolver.ResolveResource(name);
                owner = resource.Scope.GetService<IProcessOwner>();
                name = resource.Name;
            }

            var process = owner.Claim(name);
            await process.SendSignal(signal, cancellationToken).ConfigureAwait(false);
            return $"Signal {signal.Value} sent to shell process '{name}'.";
        }
        catch (Exception failure) when (
            failure is AgentRegistryException or JsonException or FormatException or IOException or InvalidOperationException or PlatformNotSupportedException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("signal")]
        public int? Signal { get; init; }
    }
}

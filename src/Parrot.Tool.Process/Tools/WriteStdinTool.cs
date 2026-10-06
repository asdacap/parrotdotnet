using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Process;

namespace Parrot.Tools;

internal sealed class WriteStdinTool(IProcessOwner processes, IAgentResolver resolver) : ITool
{
    public string Name => "write_stdin";

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
                return ToolResultFormatter.Error(invocation, "process name must not be empty");
            }

            var owner = processes;
            if (name.Contains('/', StringComparison.Ordinal))
            {
                var resource = resolver.ResolveResource(name);
                owner = resource.Scope.GetService<IProcessOwner>();
                name = resource.Name;
            }

            var result = await owner.WriteStdin(name, text, yieldAfter, cancellationToken).ConfigureAwait(false);
            return result.Format();
        }
        catch (Exception failure) when (failure is AgentRegistryException or JsonException or FormatException or InvalidOperationException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }
    }

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("input")]
        public string? Text { get; init; }

        [JsonPropertyName("yield_after_ms")]
        public long? YieldAfterMilliseconds { get; init; }
    }
}

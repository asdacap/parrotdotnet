using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentInterruptTool(IChildRegistry registry) : ITool
{
    public string Name => "agent_interrupt";

    // Interrupting a lingering child is settlement work, so the tool stays
    // offered on the final provider request.
    public bool IsEnabledAfterInterruption => true;

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        string name;
        try
        {
            var input = JsonSerializer.Deserialize(invocation.ArgumentsJson, AgentInterruptToolJsonContext.Default.AgentInterruptToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            name = (input.Name ?? throw new FormatException("Tool arguments require a string 'name'.")).Trim();
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }

        if (name.Length == 0)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, "no child agent given"));
        }

        IAgentSession childSession;
        try
        {
            childSession = registry.ResolveDirectChildScope(name).Session;
        }
        catch (AgentRegistryException failure)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }

        // Interrupt awaits the child's drain unwind, so it must not be awaited
        // on the tool path; CancellationToken.None keeps the tool call itself
        // settling while the child's cancellation is in flight.
        _ = Task.Run(() => childSession.Interrupt(CancellationToken.None), CancellationToken.None);
        return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Text(
            invocation,
            $"Interrupt requested for child agent '{name}'. Its stop is reported through its completion event."));
    }

    internal sealed class Input
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }
}

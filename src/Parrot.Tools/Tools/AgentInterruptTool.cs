using System.Text.Json;
using Parrot.Agent;

namespace Parrot.Tools;

internal sealed class AgentInterruptTool(TurnInterruptionRequest interruption) : ITool
{
    public string Name => "agent_interrupt";

    // The final provider request this tool triggers must still offer it, so a
    // second call can settle the same way the turn limit allows.
    public bool IsEnabledAfterInterruption => true;

    public Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = JsonSerializer.Deserialize(invocation.ArgumentsJson, AgentInterruptToolJsonContext.Default.AgentInterruptToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
            interruption.Request();
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Text(
                invocation,
                "Turn interruption requested. The current tool batch settles, then the next provider request is final: only the settlement tools remain available and no new work may start. Provide the best possible final answer on that request."));
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return Task.FromResult<ToolExecutionResult>(ToolResultFormatter.Error(invocation, failure.Message));
        }
    }

    internal sealed class Input;
}

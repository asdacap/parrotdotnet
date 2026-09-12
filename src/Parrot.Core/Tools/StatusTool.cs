using System.Text.Json;
using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class StatusTool(
    IRuntimeStatus status,
    IAgentSession session) : ITool
{
    public string Name => "status";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = JsonSerializer.Deserialize(invocation.ArgumentsJson, StatusToolJsonContext.Default.StatusToolInput)
                ?? throw new FormatException("Tool arguments must be an object.");
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        var runtime = await status.ObserveStatistics(session, selection, cancellationToken).ConfigureAwait(false);
        return string.IsNullOrWhiteSpace(runtime)
            ? ToolResultFormatter.Text(invocation, "No runtime status is currently available.")
            : runtime;
    }

    internal sealed class Input;
}

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
            _ = ToolInputConversion.Deserialize(invocation.ArgumentsJson, StatusToolJsonContext.Default.StatusToolInput);
        }
        catch (Exception failure) when (failure is JsonException or FormatException)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        var observed = new[]
        {
            await status.ObserveRuntime(session, selection, cancellationToken).ConfigureAwait(false),
            await status.ObserveStatistics(session, selection, cancellationToken).ConfigureAwait(false),
        };
        var runtime = string.Join("\n\n", observed.Where(text => !string.IsNullOrWhiteSpace(text)));
        return string.IsNullOrWhiteSpace(runtime)
            ? ToolResultFormatter.Text(invocation, "No runtime status is currently available.")
            : runtime;
    }

    internal sealed class Input;
}

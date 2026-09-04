using System.Text.Json;
using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class WaitTool(
    RuntimeStatus status,
    IAgentSession session,
    TimeProvider timeProvider) : ITool
{
    internal const long DefaultDurationMilliseconds = 10_000;
    internal const long MaximumDurationMilliseconds = uint.MaxValue - 1L;

    public string Name => "wait";

    public async Task<ToolExecutionResult> Execute(
        ToolInvocation invocation,
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        long durationMilliseconds;

        try
        {
            using var arguments = JsonDocument.Parse(invocation.ArgumentsJson);
            var root = arguments.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return ToolResultFormatter.Error(invocation, "Tool arguments must be an object.");
            }

            if (root.EnumerateObject().Any(property => !string.Equals(property.Name, "duration_ms", StringComparison.Ordinal)))
            {
                return ToolResultFormatter.Error(invocation, "Tool arguments contain an unexpected property.");
            }

            durationMilliseconds = DefaultDurationMilliseconds;
            if (root.TryGetProperty("duration_ms", out var duration))
            {
                if (duration.ValueKind != JsonValueKind.Number
                    || !duration.TryGetInt64(out durationMilliseconds)
                    || durationMilliseconds < DefaultDurationMilliseconds
                    || durationMilliseconds > MaximumDurationMilliseconds)
                {
                    return ToolResultFormatter.Error(invocation, $"Tool argument 'duration_ms' must be an integer from {DefaultDurationMilliseconds} through {MaximumDurationMilliseconds}.");
                }
            }
        }
        catch (JsonException failure)
        {
            return ToolResultFormatter.Error(invocation, failure.Message);
        }

        var activity = await session.WaitForIncomingInput(
            TimeSpan.FromMilliseconds(durationMilliseconds),
            timeProvider,
            cancellationToken).ConfigureAwait(false);
        if (activity is not null)
        {
            return activity.Kind switch
            {
                IncomingActivityKind.AgentCompletion => $"wait interrupted due to {activity.Name} completion",
                IncomingActivityKind.ProcessCompletion => $"wait interrupted due to process {activity.Name} completion",
                IncomingActivityKind.Input => "wait interrupted",
                _ => throw new InvalidOperationException($"Unknown incoming activity '{activity.Kind}'."),
            };
        }

        var runtime = await status.ObserveRuntime(session, selection, cancellationToken).ConfigureAwait(false);
        return $"Wait timed out after {durationMilliseconds} ms.\n\n{runtime}";
    }
}

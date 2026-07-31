using System.Text.Json;
using Parrot.Agent;
using Parrot.Statuses;

namespace Parrot.Tools;

internal sealed class WaitTool(
    RuntimeStatus status,
    AgentSession session,
    TimeProvider timeProvider) : ITool
{
    internal const long DefaultDurationMilliseconds = 10_000;
    internal const long MaximumDurationMilliseconds = uint.MaxValue - 1L;

    public string Name => "wait";

    public string Description =>
        "Wait for incoming activity. Returns early for a new message, direct child-agent completion, "
        + "unclaimed yielded-shell completion, or an item from a queue enabled with queue_listen. "
        + "A timeout reports queues, active processes, and active subagents.";

    public string ParametersJson =>
        """
        {"type":"object","properties":{"duration_ms":{"type":"integer","minimum":10000,"maximum":4294967294,"default":10000,"description":"Maximum time to wait in milliseconds."}},"additionalProperties":false}
        """;

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
                return "error: Tool arguments must be an object.";
            }

            if (root.EnumerateObject().Any(property => !string.Equals(property.Name, "duration_ms", StringComparison.Ordinal)))
            {
                return "error: Tool arguments contain an unexpected property.";
            }

            durationMilliseconds = DefaultDurationMilliseconds;
            if (root.TryGetProperty("duration_ms", out var duration))
            {
                if (duration.ValueKind != JsonValueKind.Number
                    || !duration.TryGetInt64(out durationMilliseconds)
                    || durationMilliseconds < DefaultDurationMilliseconds
                    || durationMilliseconds > MaximumDurationMilliseconds)
                {
                    return $"error: Tool argument 'duration_ms' must be an integer from {DefaultDurationMilliseconds} through {MaximumDurationMilliseconds}.";
                }
            }
        }
        catch (JsonException failure)
        {
            return $"error: {failure.Message}";
        }

        var activity = await session.WaitForIncomingInput(
            TimeSpan.FromMilliseconds(durationMilliseconds),
            timeProvider,
            cancellationToken).ConfigureAwait(false);
        if (activity)
        {
            return "Incoming activity is available.";
        }

        var activityStatus = await status.ObserveActivity(session, selection, cancellationToken).ConfigureAwait(false);
        return $"Wait timed out after {durationMilliseconds} ms.\n\n{activityStatus}";
    }
}

using System.Text.Json;
using System.Text.Json.Serialization;
using Parrot.Agent;
using Parrot.Queues;
using Parrot.Tools.Schema;

namespace Parrot.Tools;

internal sealed partial class QueueTakeTool(AgentQueues queues) : ITool
{
    public string Name => "queue_take";

    public string ParametersJson => Input.Descriptor;

    public async Task<ToolExecutionResult> Execute(ToolInvocation invocation, AgentTurnSelection selection, CancellationToken cancellationToken)
    {
        try
        {
            var input = QueueToolExecution.Deserialize(invocation.ArgumentsJson, QueueToolJsonContext.Default.QueueTakeToolInput);
            var name = QueueToolExecution.RequireName(input.Name);
            var count = input.Count ?? 1;
            var direction = QueueToolExecution.ParseDirection(input.Direction);
            var yieldAfter = input.YieldAfterMilliseconds ?? 30_000;
            if (count <= 0)
            {
                throw new FormatException("Tool argument 'count' must be a positive integer.");
            }

            if (yieldAfter is < 0 or > long.MaxValue / TimeSpan.TicksPerMillisecond)
            {
                throw new FormatException("Tool argument 'yield_after_ms' must be a non-negative integer within range.");
            }

            var deadline = Environment.TickCount64 + yieldAfter;
            QueueInfo? empty = null;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    var taken = queues.TryTake(name, count, direction);

                    if (taken.Acquired)
                    {
                        return QueueToolExecution.Serialize(
                            taken.Info ?? throw new QueueException("queue: missing take information"),
                            taken.Items);
                    }
                }
                catch (QueueEmptyException failure)
                {
                    empty = failure.Info;
                }

                var remaining = deadline - Environment.TickCount64;

                if (remaining <= 0)
                {
                    return QueueToolExecution.Serialize(empty ?? queues.Get(name), []);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(10, remaining)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is JsonException or FormatException or QueueException)
        {
            return $"error: {failure.Message}";
        }
    }

    [ToolInputModel(AdditionalPropertiesPolicy.Closed)]
    internal sealed partial class Input
    {
        [JsonPropertyName("name")]
        [ToolPattern("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
        [ToolRequired]
        public string? Name { get; init; }

        [JsonPropertyName("count")]
        [ToolDefaultLong(1)]
        [ToolMinimum(1)]
        public int? Count { get; init; }

        [JsonPropertyName("direction")]
        [ToolDefaultString("front")]
        [ToolStringEnum("front", "back")]
        public string? Direction { get; init; }

        [JsonPropertyName("yield_after_ms")]
        [ToolDefaultLong(30_000)]
        [ToolMinimum(0)]
        public long? YieldAfterMilliseconds { get; init; }
    }
}

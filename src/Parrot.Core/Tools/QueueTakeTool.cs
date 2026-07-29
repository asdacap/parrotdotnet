using System.Text.Json;
using Parrot.Queues;

namespace Parrot.Tools;

internal sealed class QueueTakeTool(QueueStore queues) : ITool
{
    public string Name => "queue_take";

    public string Description => "Remove and return strings from an existing shared user-session queue. If the queue is empty, wait until an item is available or yield_after_ms elapses. Count defaults to one, direction defaults to front, and yield_after_ms defaults to 30000. The queue does not need to fill count before returning.";

    public string ParametersJson => """{"type":"object","properties":{"name":{"type":"string","pattern":"^[a-z0-9]+(?:-[a-z0-9]+)*$"},"count":{"type":"integer","minimum":1,"default":1},"direction":{"type":"string","enum":["front","back"],"default":"front"},"yield_after_ms":{"type":"integer","minimum":0,"default":30000}},"required":["name"],"additionalProperties":false}""";

    public async Task<string> Execute(string argumentsJson, CancellationToken cancellationToken)
    {
        try
        {
            var input = QueueToolExecution.Deserialize(argumentsJson);
            var name = QueueToolExecution.RequireName(input);
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
}

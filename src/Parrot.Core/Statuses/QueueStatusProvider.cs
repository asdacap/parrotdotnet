using System.Text.Json;
using Parrot.Queues;

namespace Parrot.Statuses;

internal sealed class QueueStatusProvider(QueueStore queues) : IStatusProvider
{
    public string Key => "runtime:queues";

    public ValueTask<StatusObservation> Observe(StatusQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        var listed = queues.List();

        if (listed.Count == 0)
        {
            return ValueTask.FromResult(StatusObservation.AvailableText("Queues: none"));
        }

        var lines = listed.Select(queue =>
        {
            var description = string.IsNullOrEmpty(queue.Description)
                ? string.Empty
                : $", description: {JsonSerializer.Serialize(queue.Description, StatusJsonContext.Default.String)}";
            return $"- {queue.Name} ({queue.Size} items{description})";
        });
        return ValueTask.FromResult(StatusObservation.AvailableText($"Queues:\n{string.Join('\n', lines)}"));
    }
}

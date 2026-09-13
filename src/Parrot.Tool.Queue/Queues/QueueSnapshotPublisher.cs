using Parrot.Events;
using Parrot.Protocol;

namespace Parrot.Queues;

internal sealed class QueueSnapshotPublisher(IAgentQueues queues, IEventBroker events)
{
    public IReadOnlyList<Event> CaptureSnapshotEvents(string rootAgentSessionId) =>
        [.. QueueInventoryProtocol.Convert(queues.CaptureInventory(), rootAgentSessionId)];

    public async Task Run(string rootAgentSessionId)
    {
        using var subscription = queues.SubscribeInventory();
        await foreach (var snapshot in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            events.PublishQueueSnapshot([.. QueueInventoryProtocol.Convert(snapshot, rootAgentSessionId)]);
        }
    }
}

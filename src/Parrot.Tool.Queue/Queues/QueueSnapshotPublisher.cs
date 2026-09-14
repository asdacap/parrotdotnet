using Parrot.Events;
using Parrot.Protocol;

namespace Parrot.Queues;

internal sealed class QueueSnapshotPublisher(IAgentQueues queues, IEventBroker events, string rootAgentSessionId) : IInventoryPublisher
{
    public IReadOnlyList<Event> CaptureSnapshotEvents() =>
        [.. QueueInventoryProtocol.Convert(queues.CaptureInventory(), rootAgentSessionId)];

    public async Task Run()
    {
        using var subscription = queues.SubscribeInventory();
        await foreach (var snapshot in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            events.PublishQueueSnapshot([.. QueueInventoryProtocol.Convert(snapshot, rootAgentSessionId)]);
        }
    }
}

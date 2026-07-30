using Parrot.Protocol;
using ProtocolQueueState = Parrot.Protocol.QueueState;

namespace Parrot.Queues;

internal static class QueueInventoryProtocol
{
    private const int MaximumEventBytes = 32 * 1024 * 1024;

    public static IEnumerable<Event> Convert(QueueInventorySnapshot inventory)
    {
        if (inventory.Queues.Count == 0)
        {
            yield return Build(inventory.Revision, 0, true, null);
            yield break;
        }

        for (var index = 0; index < inventory.Queues.Count; index++)
        {
            yield return Build(
                inventory.Revision,
                checked((uint)index),
                index == inventory.Queues.Count - 1,
                inventory.Queues[index]);
        }
    }

    private static Event Build(ulong revision, uint chunkIndex, bool finalChunk, QueueState? state)
    {
        var snapshot = new Protocol.QueueSnapshot
        {
            Revision = revision,
            ChunkIndex = chunkIndex,
            FinalChunk = finalChunk,
        };
        if (state is not null)
        {
            snapshot.Queues.Add(new ProtocolQueueState
            {
                Name = state.Name,
                Description = state.Description,
                ItemCount = state.ItemCount,
            });
        }

        var published = new Event { QueueSnapshot = snapshot };
        if (published.CalculateSize() > MaximumEventBytes)
        {
            throw new QueueException($"queue: inventory record exceeds {MaximumEventBytes} bytes");
        }

        return published;
    }
}

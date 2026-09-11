using Parrot.Protocol;
using ProtocolQueueState = Parrot.Protocol.QueueState;

namespace Parrot.Queues;

internal static class QueueInventoryProtocol
{
    private const int MaximumEventBytes = 32 * 1024 * 1024;

    public static IEnumerable<Event> Convert(QueueInventorySnapshot inventory, string rootAgentSessionId)
    {
        ArgumentException.ThrowIfNullOrEmpty(rootAgentSessionId);
        if (inventory.Queues.Count == 0)
        {
            yield return Build(inventory, 0, true, rootAgentSessionId, null);
            yield break;
        }

        for (var index = 0; index < inventory.Queues.Count; index++)
        {
            yield return Build(
                inventory,
                checked((uint)index),
                index == inventory.Queues.Count - 1,
                rootAgentSessionId,
                inventory.Queues[index]);
        }
    }

    private static Event Build(
        QueueInventorySnapshot inventory,
        uint chunkIndex,
        bool finalChunk,
        string rootAgentSessionId,
        QueueState? state)
    {
        var snapshot = new Protocol.QueueSnapshot
        {
            OwnerAgentSessionId = inventory.OwnerAgentSessionId,
            InventoryInstanceId = inventory.InventoryInstanceId,
            Removed = inventory.Removed,
            Revision = inventory.Revision,
            ChunkIndex = chunkIndex,
            FinalChunk = finalChunk,
            RootAgentSessionId = rootAgentSessionId,
        };
        if (state is not null)
        {
            snapshot.Queues.Add(new ProtocolQueueState
            {
                Name = state.Name,
                Description = state.Description,
                ItemCount = state.ItemCount,
                OwnerAgentSessionId = state.OwnerAgentSessionId,
                OwnerAgentName = state.OwnerAgentName,
                ParentAgentSessionId = state.ParentAgentSessionId,
                ParentAgentName = state.ParentAgentName,
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

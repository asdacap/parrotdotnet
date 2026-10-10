using System.Threading.Channels;

namespace Parrot.Queues;

/// <summary>Reads inventory snapshots until disposal releases the subscription and completes its reader.</summary>
internal interface IQueueInventorySubscription : IDisposable
{
    ChannelReader<QueueInventorySnapshot> Reader { get; }
}

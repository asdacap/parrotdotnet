using System.Threading.Channels;

namespace Parrot.Queues;

internal sealed class QueueInventorySubscription(
    QueueInventoryFeed owner,
    Channel<QueueInventorySnapshot> channel) : IDisposable
{
    private bool _disposed;

    public ChannelReader<QueueInventorySnapshot> Reader => channel.Reader;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        owner.Unsubscribe(channel);
    }
}

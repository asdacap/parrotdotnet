using System.Threading.Channels;

namespace Parrot.Queues;

internal sealed class QueueInventoryFeed : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Channel<QueueInventorySnapshot>> _subscribers = [];
    private bool _disposed;

    public QueueInventorySubscription Subscribe(QueueInventorySnapshot initial)
    {
        var channel = Channel.CreateBounded<QueueInventorySnapshot>(
            new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        lock (_gate)
        {
            if (_disposed)
            {
                _ = channel.Writer.TryComplete();
                return new(this, channel);
            }

            _subscribers.Add(channel);
            _ = channel.Writer.TryWrite(initial);
        }

        return new(this, channel);
    }

    public void Publish(QueueInventorySnapshot snapshot)
    {
        Channel<QueueInventorySnapshot>[] targets;
        lock (_gate)
        {
            targets = _disposed ? [] : [.. _subscribers];
        }

        foreach (var target in targets)
        {
            _ = target.Writer.TryWrite(snapshot);
        }
    }

    public void Dispose()
    {
        Channel<QueueInventorySnapshot>[] targets;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            targets = [.. _subscribers];
            _subscribers.Clear();
        }

        foreach (var target in targets)
        {
            _ = target.Writer.TryComplete();
        }
    }

    internal void Unsubscribe(Channel<QueueInventorySnapshot> channel)
    {
        lock (_gate)
        {
            _ = _subscribers.Remove(channel);
        }

        _ = channel.Writer.TryComplete();
    }
}

using System.Threading.Channels;

namespace Parrot.Process;

internal sealed class ShellProcessInventoryFeed : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Channel<ShellProcessInventorySnapshot>> _subscribers = [];
    private bool _disposed;

    public IShellProcessInventorySubscription Subscribe(ShellProcessInventorySnapshot initial)
    {
        var channel = Channel.CreateBounded<ShellProcessInventorySnapshot>(
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
                return new ShellProcessInventorySubscription(this, channel);
            }

            _subscribers.Add(channel);
            _ = channel.Writer.TryWrite(initial);
        }

        return new ShellProcessInventorySubscription(this, channel);
    }

    public void Publish(ShellProcessInventorySnapshot snapshot)
    {
        Channel<ShellProcessInventorySnapshot>[] targets;
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
        Channel<ShellProcessInventorySnapshot>[] targets;
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

    internal void Unsubscribe(Channel<ShellProcessInventorySnapshot> channel)
    {
        lock (_gate)
        {
            _ = _subscribers.Remove(channel);
        }

        _ = channel.Writer.TryComplete();
    }
}

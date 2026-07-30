using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Parrot.Protocol;

namespace Parrot.Events;

internal sealed class EventBroker : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<Channel<Event>> _subscribers = [];
    private bool _disposed;

    public ValueTask Publish(Event published, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(published);
        return ValueTask.CompletedTask;
    }

    public void Publish(Event published)
    {
        Channel<Event>[] targets;
        lock (_gate)
        {
            targets = _disposed ? [] : [.. _subscribers];
        }

        foreach (var target in targets)
        {
            _ = target.Writer.TryWrite(published);
        }
    }

    public EventSubscription Subscribe()
    {
        var channel = Channel.CreateBounded<Event>(
            new BoundedChannelOptions(1024)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
            });

        lock (_gate)
        {
            if (_disposed)
            {
                _ = channel.Writer.TryComplete();
            }
            else
            {
                _subscribers.Add(channel);
            }
        }

        return new(this, channel);
    }

    public async IAsyncEnumerable<Event> Subscribe(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var subscription = Subscribe();
        await foreach (var published in subscription.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            yield return published;
        }
    }

    public void Dispose()
    {
        Channel<Event>[] targets;
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

    internal void Unsubscribe(Channel<Event> channel)
    {
        lock (_gate)
        {
            _ = _subscribers.Remove(channel);
        }

        _ = channel.Writer.TryComplete();
    }
}

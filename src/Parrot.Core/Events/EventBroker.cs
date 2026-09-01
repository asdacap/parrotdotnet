using System.Runtime.CompilerServices;
using Parrot.Protocol;

namespace Parrot.Events;

internal sealed class EventBroker : IDisposable
{
    private readonly Lock _gate = new();
    private readonly List<EventSubscription> _subscribers = [];
    private bool _disposed;

    public ValueTask Publish(Event published, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(published);
        return ValueTask.CompletedTask;
    }

    public void Publish(Event published) => Publish(published, transient: false);

    public void PublishTransient(Event published) => Publish(published, transient: true);

    public EventSubscription Subscribe()
    {
        var subscription = new EventSubscription(this);

        lock (_gate)
        {
            if (_disposed)
            {
                subscription.Complete();
            }
            else
            {
                _subscribers.Add(subscription);
            }
        }

        return subscription;
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
        EventSubscription[] targets;
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
            target.Complete();
        }
    }

    internal void Unsubscribe(EventSubscription subscription)
    {
        lock (_gate)
        {
            _ = _subscribers.Remove(subscription);
        }

        subscription.Complete();
    }

    private void Publish(Event published, bool transient)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var target in _subscribers)
            {
                target.Publish(published, transient);
            }
        }
    }
}

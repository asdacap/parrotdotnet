using System.Runtime.CompilerServices;
using Parrot.Protocol;

namespace Parrot.Events;

internal sealed class EventBroker : IEventBroker
{
    private readonly Lock _gate = new();
    private readonly List<IEventSubscription> _subscribers = [];
    private bool _disposed;

    public ValueTask PublishWithCancellation(Event published, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Publish(published);
        return ValueTask.CompletedTask;
    }

    public void Publish(Event published) => Publish(published, transient: false);

    public void PublishTransient(Event published) => Publish(published, transient: true);

    public void PublishInventory(IReadOnlyList<Event> chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            foreach (var target in _subscribers)
            {
                target.PublishInventory(chunks);
            }
        }
    }

    public IEventSubscription Subscribe()
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

    public async IAsyncEnumerable<Event> SubscribeEvents(
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
        IEventSubscription[] targets;
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

    public void Unsubscribe(IEventSubscription subscription)
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

using Parrot.Protocol;

namespace Parrot.Events;

/// <summary>Publishes committed and transient events to owned subscriptions.</summary>
internal interface IEventBroker : IDisposable
{
    ValueTask PublishWithCancellation(Event published, CancellationToken cancellationToken);

    void Publish(Event published);

    void PublishTransient(Event published);

    void PublishInventory(IReadOnlyList<Event> chunks);

    IEventSubscription Subscribe();

    IAsyncEnumerable<Event> SubscribeEvents(CancellationToken cancellationToken);

    /// <summary>Removes and completes an owned subscription.</summary>
    void Unsubscribe(IEventSubscription subscription);
}

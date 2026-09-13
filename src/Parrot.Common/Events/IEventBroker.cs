using Parrot.Protocol;

namespace Parrot.Events;

/// <summary>Publishes committed and transient events to owned subscriptions.</summary>
internal interface IEventBroker : IDisposable
{
    ValueTask PublishWithCancellation(Event published, CancellationToken cancellationToken);

    void Publish(Event published);

    void PublishTransient(Event published);

    /// <summary>Publishes a complete queue snapshot batch; every event must contain a queue snapshot.</summary>
    void PublishQueueSnapshot(IReadOnlyList<Event> chunks);

    /// <summary>Publishes a complete process snapshot batch; every event must contain a shell process snapshot.</summary>
    void PublishProcessSnapshot(IReadOnlyList<Event> chunks);

    IEventSubscription Subscribe();

    IAsyncEnumerable<Event> SubscribeEvents(CancellationToken cancellationToken);

    /// <summary>Removes and completes an owned subscription.</summary>
    void Unsubscribe(IEventSubscription subscription);
}

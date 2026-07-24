using Parrot.Events;
using Parrot.Protocol;

namespace Parrot.Core.Tests;

internal sealed class EventBrokerTests
{
    [Test]
    public async Task Publishing_with_no_subscriber_reaches_nobody_and_does_not_block(
        CancellationToken cancellationToken)
    {
        using var broker = new EventBroker();

        // A single shared bounded channel would fill and stall here, which is
        // what made a dropped listener look like a hang rather than a leak.
        for (var index = 0; index < 5000; index++)
        {
            await broker.Publish(new Event { Id = $"{index}" }, cancellationToken);
        }

        var subscription = broker.Subscribe(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            var pending = subscription.MoveNextAsync();
            await broker.Publish(new Event { Id = "after" }, cancellationToken);

            // Nothing published before this subscription existed is replayed.
            _ = await Assert.That(await pending).IsTrue();
            _ = await Assert.That(subscription.Current.Id).IsEqualTo("after");
        }
        finally
        {
            await subscription.DisposeAsync();
        }
    }

    [Test]
    public async Task A_departed_subscriber_stops_receiving(CancellationToken cancellationToken)
    {
        using var broker = new EventBroker();
        var subscription = broker.Subscribe(cancellationToken).GetAsyncEnumerator(cancellationToken);

        var pending = subscription.MoveNextAsync();
        await broker.Publish(new Event { Id = "first" }, cancellationToken);
        _ = await pending;

        // Disposing the enumerator is how a listener leaves.
        await subscription.DisposeAsync();

        // Reaches nobody, and still returns rather than queueing forever
        // against a subscriber that will never read again.
        await broker.Publish(new Event { Id = "second" }, cancellationToken);

        // A fresh subscriber sees only what is published from now on, which is
        // how we know the departed queue was dropped rather than replayed.
        var second = broker.Subscribe(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            var pendingSecond = second.MoveNextAsync();
            await broker.Publish(new Event { Id = "third" }, cancellationToken);

            _ = await Assert.That(await pendingSecond).IsTrue();
            _ = await Assert.That(second.Current.Id).IsEqualTo("third");
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Test]
    public async Task Two_subscribers_both_receive(CancellationToken cancellationToken)
    {
        using var broker = new EventBroker();
        var first = broker.Subscribe(cancellationToken).GetAsyncEnumerator(cancellationToken);
        var second = broker.Subscribe(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            var firstPending = first.MoveNextAsync();
            var secondPending = second.MoveNextAsync();

            await broker.Publish(new Event { Id = "fanned" }, cancellationToken);

            _ = await Assert.That(await firstPending).IsTrue();
            _ = await Assert.That(await secondPending).IsTrue();
            _ = await Assert.That(first.Current.Id).IsEqualTo("fanned");
            _ = await Assert.That(second.Current.Id).IsEqualTo("fanned");
        }
        finally
        {
            await first.DisposeAsync();
            await second.DisposeAsync();
        }
    }
}

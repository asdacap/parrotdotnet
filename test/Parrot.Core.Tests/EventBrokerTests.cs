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
            await broker.PublishWithCancellation(new Event { Id = $"{index}" }, cancellationToken);
        }

        var subscription = broker.SubscribeEvents(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            var pending = subscription.MoveNextAsync();
            await broker.PublishWithCancellation(new Event { Id = "after" }, cancellationToken);

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
    public async Task Eager_subscription_receives_before_reading(CancellationToken cancellationToken)
    {
        using var broker = new EventBroker();
        using var subscription = broker.Subscribe();

        await broker.PublishWithCancellation(new Event { Id = "ready" }, cancellationToken);
        var published = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await Assert.That(published.Id).IsEqualTo("ready");
    }

    [Test]
    public async Task A_departed_subscriber_stops_receiving(CancellationToken cancellationToken)
    {
        using var broker = new EventBroker();
        var subscription = broker.SubscribeEvents(cancellationToken).GetAsyncEnumerator(cancellationToken);

        var pending = subscription.MoveNextAsync();
        await broker.PublishWithCancellation(new Event { Id = "first" }, cancellationToken);
        _ = await pending;

        // Disposing the enumerator is how a listener leaves.
        await subscription.DisposeAsync();

        // Reaches nobody, and still returns rather than queueing forever
        // against a subscriber that will never read again.
        await broker.PublishWithCancellation(new Event { Id = "second" }, cancellationToken);

        // A fresh subscriber sees only what is published from now on, which is
        // how we know the departed queue was dropped rather than replayed.
        var second = broker.SubscribeEvents(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            var pendingSecond = second.MoveNextAsync();
            await broker.PublishWithCancellation(new Event { Id = "third" }, cancellationToken);

            _ = await Assert.That(await pendingSecond).IsTrue();
            _ = await Assert.That(second.Current.Id).IsEqualTo("third");
        }
        finally
        {
            await second.DisposeAsync();
        }
    }

    [Test]
    public async Task Transient_publication_only_displaces_older_transient_events()
    {
        using var broker = new EventBroker();
        using var subscription = broker.Subscribe();

        for (var index = 0; index < 1024; index++)
        {
            broker.Publish(new Event { Id = $"retained-{index}" });
            broker.PublishTransient(new Event { Id = $"transient-{index}" });
        }

        broker.PublishTransient(new Event { Id = "transient-new" });

        var received = new List<string>();
        while (subscription.Reader.TryRead(out var published))
        {
            received.Add(published.Id);
        }

        _ = await Assert.That(received).Count().IsEqualTo(2048);
        _ = await Assert.That(received.Where(id => id.StartsWith("retained-", StringComparison.Ordinal))).Count().IsEqualTo(1024);
        _ = await Assert.That(received).Contains("retained-0");
        _ = await Assert.That(received).Contains("retained-1023");
        _ = await Assert.That(received).DoesNotContain("transient-0");
        _ = await Assert.That(received).Contains("transient-new");
    }

    [Test]
    public async Task Inventory_revisions_coalesce_by_owner_and_type_without_truncating_chunks_or_evicting_events()
    {
        using var broker = new EventBroker();
        using var subscription = broker.Subscribe();
        for (var index = 0; index < 1024; index++)
        {
            broker.Publish(new Event { Id = $"ordinary-{index}" });
        }

        var obsolete = new Event { QueueSnapshot = new QueueSnapshot { OwnerAgentSessionId = "first", Revision = 1 } };
        broker.PublishInventory([obsolete]);
        var sibling = new Event { QueueSnapshot = new QueueSnapshot { OwnerAgentSessionId = "second", Revision = 1, FinalChunk = true } };
        broker.PublishInventory([sibling]);
        var processes = new Event { ShellProcessSnapshot = new ShellProcessSnapshot { OwnerAgentSessionId = "first", Revision = 1, ChunkCount = 1 } };
        broker.PublishInventory([processes]);
        var replacement = Enumerable.Range(0, 1100).Select(index => new Event
        {
            QueueSnapshot = new QueueSnapshot
            {
                OwnerAgentSessionId = "first",
                InventoryInstanceId = "instance",
                Revision = 2,
                ChunkIndex = (uint)index,
                FinalChunk = index == 1099,
            },
        }).ToArray();
        broker.PublishInventory(replacement);

        var received = new List<Event>();
        while (subscription.Reader.TryRead(out var published))
        {
            received.Add(published);
        }

        _ = await Assert.That(received).Count().IsEqualTo(2126);
        _ = await Assert.That(received).DoesNotContain(obsolete);
        _ = await Assert.That(received).Contains(sibling).And.Contains(processes);
        _ = await Assert.That(received.Where(item => item.QueueSnapshot?.OwnerAgentSessionId == "first").SequenceEqual(replacement)).IsTrue();
        _ = await Assert.That(received.Where(item => item.QueueSnapshot?.OwnerAgentSessionId == "first")
            .Select(item => item.QueueSnapshot.ChunkIndex).SequenceEqual(Enumerable.Range(0, 1100).Select(index => (uint)index))).IsTrue();
        _ = await Assert.That(received.Where(item => item.PayloadCase == Event.PayloadOneofCase.None).Select(item => item.Id).SequenceEqual(Enumerable.Range(0, 1024).Select(index => $"ordinary-{index}"))).IsTrue();
    }

    [Test]
    public async Task Active_inventory_finishes_before_replacement_and_replenishment_does_not_starve_ordinary_events()
    {
        using var broker = new EventBroker();
        using var subscription = broker.Subscribe();
        var original = Enumerable.Range(0, 3).Select(index => new Event
        {
            QueueSnapshot = new QueueSnapshot
            {
                OwnerAgentSessionId = "owner",
                Revision = 1,
                ChunkIndex = (uint)index,
                FinalChunk = index == 2,
            },
        }).ToArray();
        broker.PublishInventory(original);
        _ = await Assert.That(subscription.Reader.TryRead(out var first)).IsTrue();
        _ = await Assert.That(first).IsEqualTo(original[0]);
        broker.Publish(new Event { Id = "ordinary-first" });
        broker.Publish(new Event { Id = "ordinary-second" });
        for (var index = 1; index < original.Length; index++)
        {
            broker.PublishInventory([new Event
            {
                QueueSnapshot = new QueueSnapshot { OwnerAgentSessionId = "owner", Revision = (ulong)(index + 1), FinalChunk = true },
            }
            ]);
            _ = await Assert.That(subscription.Reader.TryRead(out var chunk)).IsTrue();
            _ = await Assert.That(chunk).IsEqualTo(original[index]);
        }

        var ordinary = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            broker.PublishInventory([new Event
            {
                QueueSnapshot = new QueueSnapshot { OwnerAgentSessionId = "owner", Revision = (ulong)(index + 10), FinalChunk = true },
            }
            ]);
            _ = await Assert.That(subscription.Reader.TryRead(out var published)).IsTrue();
            if (published is { PayloadCase: Event.PayloadOneofCase.None })
            {
                ordinary.Add(published.Id);
            }
        }

        _ = await Assert.That(ordinary.SequenceEqual(["ordinary-first", "ordinary-second"])).IsTrue();
    }

    [Test]
    public async Task Two_subscribers_both_receive(CancellationToken cancellationToken)
    {
        using var broker = new EventBroker();
        var first = broker.SubscribeEvents(cancellationToken).GetAsyncEnumerator(cancellationToken);
        var second = broker.SubscribeEvents(cancellationToken).GetAsyncEnumerator(cancellationToken);

        try
        {
            var firstPending = first.MoveNextAsync();
            var secondPending = second.MoveNextAsync();

            await broker.PublishWithCancellation(new Event { Id = "fanned" }, cancellationToken);

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

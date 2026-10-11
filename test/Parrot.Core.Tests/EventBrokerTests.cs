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

        var obsolete = TestToolSnapshots.Queue(new ToolSnapshot { OwnerAgentSessionId = "first", Revision = 1 }, new QueueSnapshot());
        broker.PublishInventory([obsolete]);
        var sibling = TestToolSnapshots.Queue(new ToolSnapshot { OwnerAgentSessionId = "second", Revision = 1, FinalChunk = true }, new QueueSnapshot());
        broker.PublishInventory([sibling]);
        var processes = TestToolSnapshots.Process(new ToolSnapshot { OwnerAgentSessionId = "first", Revision = 1, FinalChunk = true }, new ShellProcessSnapshot());
        broker.PublishInventory([processes]);
        var replacement = Enumerable.Range(0, 1100).Select(index => TestToolSnapshots.Queue(
            new ToolSnapshot
            {
                OwnerAgentSessionId = "first",
                InventoryInstanceId = "instance",
                Revision = 2,
                ChunkIndex = (uint)index,
                FinalChunk = index == 1099,
            },
            new QueueSnapshot())).ToArray();
        broker.PublishInventory(replacement);

        var received = new List<Event>();
        while (subscription.Reader.TryRead(out var published))
        {
            received.Add(published);
        }

        _ = await Assert.That(received).Count().IsEqualTo(2126);
        _ = await Assert.That(received).DoesNotContain(obsolete);
        _ = await Assert.That(received).Contains(sibling).And.Contains(processes);
        var firstQueue = received.Where(item => item.ToolSnapshot is { Kind: "queue", OwnerAgentSessionId: "first" }).ToArray();
        _ = await Assert.That(firstQueue.SequenceEqual(replacement)).IsTrue();
        _ = await Assert.That(firstQueue
            .Select(item => item.ToolSnapshot.ChunkIndex).SequenceEqual(Enumerable.Range(0, 1100).Select(index => (uint)index))).IsTrue();
        _ = await Assert.That(received.Where(item => item.PayloadCase == Event.PayloadOneofCase.None).Select(item => item.Id).SequenceEqual(Enumerable.Range(0, 1024).Select(index => $"ordinary-{index}"))).IsTrue();
    }

    [Test]
    public async Task Active_inventory_finishes_before_replacement_and_replenishment_does_not_starve_ordinary_events()
    {
        using var broker = new EventBroker();
        using var subscription = broker.Subscribe();
        var original = Enumerable.Range(0, 3).Select(index => TestToolSnapshots.Queue(
            new ToolSnapshot { OwnerAgentSessionId = "owner", Revision = 1, ChunkIndex = (uint)index, FinalChunk = index == 2 },
            new QueueSnapshot())).ToArray();
        broker.PublishInventory(original);
        _ = await Assert.That(subscription.Reader.TryRead(out var first)).IsTrue();
        _ = await Assert.That(first).IsEqualTo(original[0]);
        broker.Publish(new Event { Id = "ordinary-first" });
        broker.Publish(new Event { Id = "ordinary-second" });
        for (var index = 1; index < original.Length; index++)
        {
            broker.PublishInventory([TestToolSnapshots.Queue(new ToolSnapshot { OwnerAgentSessionId = "owner", Revision = (ulong)(index + 1), FinalChunk = true }, new QueueSnapshot())]);
            _ = await Assert.That(subscription.Reader.TryRead(out var chunk)).IsTrue();
            _ = await Assert.That(chunk).IsEqualTo(original[index]);
        }

        var ordinary = new List<string>();
        for (var index = 0; index < 4; index++)
        {
            broker.PublishInventory([TestToolSnapshots.Queue(new ToolSnapshot { OwnerAgentSessionId = "owner", Revision = (ulong)(index + 10), FinalChunk = true }, new QueueSnapshot())]);
            _ = await Assert.That(subscription.Reader.TryRead(out var published)).IsTrue();
            if (published is { PayloadCase: Event.PayloadOneofCase.None })
            {
                ordinary.Add(published.Id);
            }
        }

        _ = await Assert.That(ordinary.SequenceEqual(["ordinary-first", "ordinary-second"])).IsTrue();
    }

    [Test]
    [Arguments(true, true)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(false, false)]
    public async Task Inventory_publication_rejects_a_batch_of_mixed_inventories_before_enqueue(
        bool queueFirst,
        bool directSubscription)
    {
        using var broker = new EventBroker();
        using var subscription = broker.Subscribe();
        var queue = TestToolSnapshots.Queue(new ToolSnapshot { OwnerAgentSessionId = "owner", FinalChunk = true }, new QueueSnapshot());
        var process = TestToolSnapshots.Process(new ToolSnapshot { OwnerAgentSessionId = "owner", FinalChunk = true }, new ShellProcessSnapshot());
        Event[] mixed = queueFirst ? [queue, process] : [process, queue];
        Action publish = directSubscription
            ? () => subscription.PublishInventory(mixed)
            : () => broker.PublishInventory(mixed);

        _ = await Assert.That(publish).Throws<InvalidOperationException>();
        _ = await Assert.That(subscription.Reader.TryRead(out _)).IsFalse();

        broker.PublishInventory([mixed[0]]);

        _ = await Assert.That(subscription.Reader.TryRead(out var published)).IsTrue();
        _ = await Assert.That(published).IsEqualTo(mixed[0]);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Inventory_publication_with_no_subscriber_still_validates_payload(bool mixedOwners)
    {
        using var broker = new EventBroker();
        Event[] wrong = mixedOwners
            ? [TestToolSnapshots.Queue(new ToolSnapshot { OwnerAgentSessionId = "first", FinalChunk = true }, new QueueSnapshot()), TestToolSnapshots.Queue(new ToolSnapshot { OwnerAgentSessionId = "second", FinalChunk = true }, new QueueSnapshot())]
            : [new Event { TextChunk = new TextChunk { Fragment = "not a snapshot" } }];

        _ = await Assert.That(() => broker.PublishInventory(wrong)).Throws<InvalidOperationException>();
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Empty_inventory_publication_is_a_no_op(bool directSubscription)
    {
        using var broker = new EventBroker();
        using var subscription = broker.Subscribe();

        if (directSubscription)
        {
            subscription.PublishInventory([]);
        }
        else
        {
            broker.PublishInventory([]);
        }

        _ = await Assert.That(subscription.Reader.TryRead(out _)).IsFalse();
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

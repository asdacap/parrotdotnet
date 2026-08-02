using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Core.Tests;

internal sealed class QueueStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "parrot-queue-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Test]
    public async Task Queue_lifecycle_preserves_JSONL_and_direction_order(CancellationToken cancellationToken)
    {
        using var store = new QueueStore(_directory);
        var created = store.Create("build-work-now", "release tasks");
        _ = await Assert.That(await File.ReadAllTextAsync(created.Path, cancellationToken))
            .IsEqualTo("{\"name\":\"build-work-now\",\"description\":\"release tasks\"}\n");

        _ = store.Push("build-work-now", ["one", "three"], QueueDirection.Unspecified, false);
        _ = store.Push("build-work-now", ["zero", "two"], QueueDirection.Front, false);
        var back = await store.Take("build-work-now", 2, QueueDirection.Back, cancellationToken);
        var front = await store.Take("build-work-now", 5, QueueDirection.Unspecified, cancellationToken);

        _ = await Assert.That(string.Join(",", back.Items)).IsEqualTo("three,one");
        _ = await Assert.That(string.Join(",", front.Items)).IsEqualTo("two,zero");
        _ = await Assert.That(store.Get("build-work-now", "agent-a").Size).IsEqualTo(0);
        _ = await Assert.That(() => store.TryTake("build-work-now", 1, QueueDirection.Front))
            .Throws<QueueEmptyException>();
    }

    [Test]
    public async Task Monitored_delivery_retries_with_the_same_id_and_removes_only_when_accepted(
        CancellationToken cancellationToken)
    {
        using var store = new QueueStore(_directory);
        _ = store.Create("alpha-work", string.Empty);
        _ = store.Push("alpha-work", ["first", "second"], QueueDirection.Back, false);
        _ = store.Monitor("alpha-work", "agent-a", true);
        _ = store.Monitor("alpha-work", "agent-b", true);
        var ids = new List<string>();

        var rejected = await store.DeliverMonitored(
            "agent-a",
            (notification, _) =>
            {
                ids.Add(notification.Id);
                return Task.FromResult(false);
            },
            cancellationToken);
        var wrongListener = await store.DeliverMonitored(
            "agent-b",
            (_, _) => Task.FromResult(true),
            cancellationToken);
        var accepted = await store.DeliverMonitored(
            "agent-a",
            (notification, _) =>
            {
                ids.Add(notification.Id);
                return Task.FromResult(true);
            },
            cancellationToken);

        _ = await Assert.That(rejected).IsFalse();
        _ = await Assert.That(wrongListener).IsFalse();
        _ = await Assert.That(accepted).IsTrue();
        _ = await Assert.That(ids[0]).IsEqualTo(ids[1]);
        _ = await Assert.That(ids[0]).StartsWith("qnt-");
        _ = await Assert.That(store.Get("alpha-work", "agent-a").Size).IsEqualTo(1);
        _ = await Assert.That(store.Get("alpha-work", "agent-a").Monitored).IsTrue();
        _ = await Assert.That(store.Get("alpha-work", "agent-c").Monitored).IsFalse();
    }

    [Test]
    public async Task Listener_queries_are_distinct_sorted_and_persisted()
    {
        using (var store = new QueueStore(_directory))
        {
            _ = store.Create("alpha-work", string.Empty);
            _ = store.Create("beta-work", string.Empty);
            _ = store.Monitor("alpha-work", "agent-b", true);
            _ = store.Monitor("alpha-work", "agent-a", true);
            _ = store.Monitor("beta-work", "agent-b", true);
            _ = store.Monitor("alpha-work", "agent-b", false);
        }

        using var restored = new QueueStore(_directory);
        _ = await Assert.That(string.Join(',', restored.ListenerSessionIds("alpha-work"))).IsEqualTo("agent-a");
        _ = await Assert.That(string.Join(',', restored.ListenerSessionIds())).IsEqualTo("agent-a,agent-b");
        _ = await Assert.That(restored.List("agent-a").Single(queue => queue.Name == "alpha-work").Monitored)
            .IsTrue();
        _ = await Assert.That(restored.List("agent-b").Single(queue => queue.Name == "alpha-work").Monitored)
            .IsFalse();
    }

    [Test]
    public async Task Inventory_replays_persisted_state_and_tracks_visible_mutations(
        CancellationToken cancellationToken)
    {
        using (var persisted = new QueueStore(_directory))
        {
            _ = persisted.Create("zeta", "later");
            _ = persisted.Push("zeta", ["one", "two"], QueueDirection.Back, false);
            _ = persisted.Create("alpha", "first");
            _ = persisted.Push("alpha", ["one"], QueueDirection.Back, false);
            _ = persisted.Create("empty", "hidden");
        }

        using var inventory = new QueueInventory();
        using var store = new QueueStore(_directory);
        store.AttachInventory(AgentIdentity.Main("agent-owner", "main"), inventory);
        using var subscription = inventory.Subscribe();
        var initial = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await Assert.That(string.Join(",", initial.Queues.Select(queue => queue.Name))).IsEqualTo("alpha,zeta");
        _ = await Assert.That(initial.Queues[0].Description).IsEqualTo("first");
        _ = await Assert.That(initial.Queues[0].OwnerAgentSessionId).IsEqualTo("agent-owner");
        _ = await Assert.That(initial.Queues[1].ItemCount).IsEqualTo(2);

        _ = await store.Take("alpha", 1, QueueDirection.Front, cancellationToken);
        var changed = await subscription.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(changed.Revision).IsGreaterThan(initial.Revision);
        _ = await Assert.That(string.Join(",", changed.Queues.Select(queue => queue.Name))).IsEqualTo("zeta");
    }

    [Test]
    public async Task Inventory_coalesces_to_latest_including_empty(CancellationToken cancellationToken)
    {
        using var inventory = new QueueInventory();
        using var store = new QueueStore(_directory);
        _ = store.Create("work", "tasks");
        store.AttachInventory(AgentIdentity.Main("agent-owner", "main"), inventory);
        using var subscription = inventory.Subscribe();
        _ = await subscription.Reader.ReadAsync(cancellationToken);

        _ = store.Push("work", ["one"], QueueDirection.Back, false);
        _ = store.Push("work", ["two"], QueueDirection.Back, false);
        _ = await store.Take("work", 2, QueueDirection.Front, cancellationToken);

        var latest = await subscription.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(latest.Queues).IsEmpty();
    }

    [Test]
    public async Task Close_is_persistent_idempotent_and_preserves_items_and_listeners(
        CancellationToken cancellationToken)
    {
        using (var store = new QueueStore(_directory))
        {
            _ = store.Create("closing-work", "finish it");
            _ = store.Monitor("closing-work", "agent-a", true);

            var closed = store.Push("closing-work", ["one", "two"], QueueDirection.Back, true);
            var closedAgain = store.Push("closing-work", [], QueueDirection.Back, true);

            _ = await Assert.That(closed.Closed).IsTrue();
            _ = await Assert.That(closed.Size).IsEqualTo(2);
            _ = await Assert.That(closedAgain).IsEqualTo(closed);
            _ = await Assert.That(store.Get("closing-work", "agent-a").Monitored).IsTrue();
            var persisted = await File.ReadAllTextAsync(closed.Path, cancellationToken);
            _ = await Assert.That(() => store.Push("closing-work", ["late"], QueueDirection.Back, false))
                .Throws<QueueClosedException>()
                .WithMessage("queue: 'closing-work' is closed");
            var idempotent = store.Push("closing-work", [], QueueDirection.Back, true);
            _ = await Assert.That(idempotent).IsEqualTo(closed);
            _ = await Assert.That(() => store.Push("closing-work", [], QueueDirection.Back, false))
                .Throws<QueueClosedException>()
                .WithMessage("queue: 'closing-work' is closed");
            _ = await Assert.That(await File.ReadAllTextAsync(closed.Path, cancellationToken)).IsEqualTo(persisted);
        }

        using var restored = new QueueStore(_directory);
        var info = restored.Get("closing-work", "agent-a");
        var taken = await restored.Take("closing-work", 5, QueueDirection.Front, cancellationToken);
        var completed = restored.TryTake("closing-work", 1, QueueDirection.Front);

        _ = await Assert.That(info.Closed).IsTrue();
        _ = await Assert.That(info.Monitored).IsTrue();
        _ = await Assert.That(string.Join(',', taken.Items)).IsEqualTo("one,two");
        _ = await Assert.That(taken.Info.Closed).IsTrue();
        _ = await Assert.That(completed.Acquired).IsTrue();
        _ = await Assert.That(completed.Items).IsEmpty();
        _ = await Assert.That(completed.Info?.Closed).IsTrue();
    }

    [Test]
    public async Task Metadata_without_closed_field_remains_open(CancellationToken cancellationToken)
    {
        _ = Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, "legacy-work.jsonl");
        await File.WriteAllTextAsync(
            path,
            "{\"name\":\"legacy-work\",\"description\":\"legacy\"}\n",
            cancellationToken);
        using var store = new QueueStore(_directory);

        var info = store.Get("legacy-work", "agent-a");
        _ = store.Push("legacy-work", ["accepted"], QueueDirection.Back, false);

        _ = await Assert.That(info.Closed).IsFalse();
        _ = await Assert.That(store.Get("legacy-work", "agent-a").Size).IsEqualTo(1);
    }

    [Test]
    public async Task Disposed_store_rejects_operations()
    {
        var store = new QueueStore(_directory);
        _ = store.Create("work", "tasks");
        store.Dispose();

        _ = await Assert.That(() => store.Push("work", ["late"], QueueDirection.Back, false))
            .Throws<ObjectDisposedException>();
        _ = await Assert.That(() => store.List("agent-owner"))
            .Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task A_filesystem_lock_bounds_take_and_try_take_does_not_wait(CancellationToken cancellationToken)
    {
        using var store = new QueueStore(_directory);
        var info = store.Create("locked-work", string.Empty);
        _ = Directory.CreateDirectory(info.Path + ".lock");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMilliseconds(20));

        _ = await Assert.That(async () =>
            await store.Take("locked-work", 1, QueueDirection.Front, timeout.Token))
            .Throws<OperationCanceledException>();
        var attempted = store.TryTake("locked-work", 1, QueueDirection.Front);
        _ = await Assert.That(attempted.Acquired).IsFalse();
    }
}

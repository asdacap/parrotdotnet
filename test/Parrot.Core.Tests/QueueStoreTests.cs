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

        _ = store.Push("build-work-now", ["one", "three"], QueueDirection.Unspecified);
        _ = store.Push("build-work-now", ["zero", "two"], QueueDirection.Front);
        var back = await store.Take("build-work-now", 2, QueueDirection.Back, cancellationToken);
        var front = await store.Take("build-work-now", 5, QueueDirection.Unspecified, cancellationToken);

        _ = await Assert.That(string.Join(",", back.Items)).IsEqualTo("three,one");
        _ = await Assert.That(string.Join(",", front.Items)).IsEqualTo("two,zero");
        _ = await Assert.That(store.Get("build-work-now").Size).IsEqualTo(0);
        _ = await Assert.That(() => store.TryTake("build-work-now", 1, QueueDirection.Front))
            .Throws<QueueEmptyException>();
    }

    [Test]
    public async Task Monitored_delivery_retries_with_the_same_id_and_removes_only_when_accepted(
        CancellationToken cancellationToken)
    {
        using var store = new QueueStore(_directory);
        _ = store.Create("alpha-work", string.Empty);
        _ = store.Push("alpha-work", ["first", "second"], QueueDirection.Back);
        _ = store.Monitor("alpha-work", true);
        var ids = new List<string>();

        var rejected = await store.DeliverMonitored(
            (notification, _) =>
            {
                ids.Add(notification.Id);
                return Task.FromResult(false);
            },
            cancellationToken);
        var accepted = await store.DeliverMonitored(
            (notification, _) =>
            {
                ids.Add(notification.Id);
                return Task.FromResult(true);
            },
            cancellationToken);

        _ = await Assert.That(rejected).IsFalse();
        _ = await Assert.That(accepted).IsTrue();
        _ = await Assert.That(ids[0]).IsEqualTo(ids[1]);
        _ = await Assert.That(ids[0]).StartsWith("qnt-");
        _ = await Assert.That(store.Get("alpha-work").Size).IsEqualTo(1);
        _ = await Assert.That(store.Get("alpha-work").Monitored).IsTrue();
    }

    [Test]
    public async Task Inventory_replays_persisted_state_and_tracks_visible_mutations(
        CancellationToken cancellationToken)
    {
        using (var persisted = new QueueStore(_directory))
        {
            _ = persisted.Create("zeta", "later");
            _ = persisted.Push("zeta", ["one", "two"], QueueDirection.Back);
            _ = persisted.Create("alpha", "first");
            _ = persisted.Push("alpha", ["one"], QueueDirection.Back);
            _ = persisted.Create("empty", "hidden");
        }

        using var store = new QueueStore(_directory);
        using var subscription = store.SubscribeInventory();
        var initial = await subscription.Reader.ReadAsync(cancellationToken);

        _ = await Assert.That(string.Join(",", initial.Queues.Select(queue => queue.Name))).IsEqualTo("alpha,zeta");
        _ = await Assert.That(initial.Queues[0].Description).IsEqualTo("first");
        _ = await Assert.That(initial.Queues[1].ItemCount).IsEqualTo(2);

        _ = await store.Take("alpha", 1, QueueDirection.Front, cancellationToken);
        var changed = await subscription.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(changed.Revision).IsGreaterThan(initial.Revision);
        _ = await Assert.That(string.Join(",", changed.Queues.Select(queue => queue.Name))).IsEqualTo("zeta");
    }

    [Test]
    public async Task Inventory_coalesces_to_latest_including_empty(CancellationToken cancellationToken)
    {
        using var store = new QueueStore(_directory);
        _ = store.Create("work", "tasks");
        using var subscription = store.SubscribeInventory();
        _ = await subscription.Reader.ReadAsync(cancellationToken);

        _ = store.Push("work", ["one"], QueueDirection.Back);
        _ = store.Push("work", ["two"], QueueDirection.Back);
        _ = await store.Take("work", 2, QueueDirection.Front, cancellationToken);

        var latest = await subscription.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(latest.Queues).IsEmpty();
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

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
        using IQueueStore store = new QueueStore(_directory);
        var created = store.Create("build-work-now", "release tasks");
        _ = await Assert.That(await File.ReadAllTextAsync(created.Path, cancellationToken))
            .IsEqualTo("{\"name\":\"build-work-now\",\"description\":\"release tasks\"}\n");

        _ = store.Push("build-work-now", ["one", "three"], QueueDirection.Unspecified, false);
        _ = store.Push("build-work-now", ["zero", "two"], QueueDirection.Front, false);
        var back = await store.Take("build-work-now", 2, QueueDirection.Back, cancellationToken);
        var front = await store.Take("build-work-now", 5, QueueDirection.Unspecified, cancellationToken);

        _ = await Assert.That(string.Join(",", back.Items)).IsEqualTo("three,one");
        _ = await Assert.That(string.Join(",", front.Items)).IsEqualTo("two,zero");
        _ = await Assert.That(store.Get("build-work-now").Size).IsEqualTo(0);
        _ = await Assert.That(() => store.TryTake("build-work-now", 1, QueueDirection.Front))
            .Throws<QueueEmptyException>();
    }

    [Test]
    public async Task Inventory_replays_persisted_state_and_tracks_visible_mutations(
        CancellationToken cancellationToken)
    {
        using (IQueueStore persisted = new QueueStore(_directory))
        {
            _ = persisted.Create("zeta", "later");
            _ = persisted.Push("zeta", ["one", "two"], QueueDirection.Back, false);
            _ = persisted.Create("alpha", "first");
            _ = persisted.Push("alpha", ["one"], QueueDirection.Back, false);
            _ = persisted.Create("empty", "hidden");
        }

        using IQueueInventory inventory = new QueueInventory(AgentIdentity.Main("agent-owner", "main", TestModels.PromptTemplates));
        using IQueueStore store = new QueueStore(_directory);
        store.AttachInventory(AgentIdentity.Main("agent-owner", "main", TestModels.PromptTemplates), inventory);
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
        using IQueueInventory inventory = new QueueInventory(AgentIdentity.Main("agent-owner", "main", TestModels.PromptTemplates));
        using IQueueStore store = new QueueStore(_directory);
        _ = store.Create("work", "tasks");
        store.AttachInventory(AgentIdentity.Main("agent-owner", "main", TestModels.PromptTemplates), inventory);
        using var subscription = inventory.Subscribe();
        _ = await subscription.Reader.ReadAsync(cancellationToken);

        _ = store.Push("work", ["one"], QueueDirection.Back, false);
        _ = store.Push("work", ["two"], QueueDirection.Back, false);
        _ = await store.Take("work", 2, QueueDirection.Front, cancellationToken);

        var latest = await subscription.Reader.ReadAsync(cancellationToken);
        _ = await Assert.That(latest.Queues).IsEmpty();
    }

    [Test]
    public async Task Close_is_persistent_idempotent_and_preserves_items(
        CancellationToken cancellationToken)
    {
        using (IQueueStore store = new QueueStore(_directory))
        {
            _ = store.Create("closing-work", "finish it");

            var closed = store.Push("closing-work", ["one", "two"], QueueDirection.Back, true);
            var closedAgain = store.Push("closing-work", [], QueueDirection.Back, true);

            _ = await Assert.That(closed.Closed).IsTrue();
            _ = await Assert.That(closed.Size).IsEqualTo(2);
            _ = await Assert.That(closedAgain).IsEqualTo(closed);
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

        using IQueueStore restored = new QueueStore(_directory);
        var info = restored.Get("closing-work");
        var taken = await restored.Take("closing-work", 5, QueueDirection.Front, cancellationToken);
        var completed = restored.TryTake("closing-work", 1, QueueDirection.Front);

        _ = await Assert.That(info.Closed).IsTrue();
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
        using IQueueStore store = new QueueStore(_directory);

        var info = store.Get("legacy-work");
        _ = store.Push("legacy-work", ["accepted"], QueueDirection.Back, false);

        _ = await Assert.That(info.Closed).IsFalse();
        _ = await Assert.That(store.Get("legacy-work").Size).IsEqualTo(1);
    }

    [Test]
    public async Task Disposed_store_rejects_operations()
    {
        IQueueStore store = new QueueStore(_directory);
        _ = store.Create("work", "tasks");
        store.Dispose();

        _ = await Assert.That(() => store.Push("work", ["late"], QueueDirection.Back, false))
            .Throws<ObjectDisposedException>();
        _ = await Assert.That(store.List).Throws<ObjectDisposedException>();
    }

    [Test]
    public async Task A_filesystem_lock_bounds_take_and_try_take_does_not_wait(CancellationToken cancellationToken)
    {
        using IQueueStore store = new QueueStore(_directory);
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

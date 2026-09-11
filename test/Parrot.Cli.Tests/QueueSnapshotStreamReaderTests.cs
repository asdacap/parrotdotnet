using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class QueueSnapshotStreamReaderTests
{
    [Test]
    public async Task Applies_only_complete_ordered_replacements_and_preserves_queue_owners(
        CancellationToken cancellationToken)
    {
        var source = new ChannelStreamWriter<Event>();
        var applied = new List<(string RootAgentSessionId, IReadOnlyList<QueueState> Queues)>();
        var reader = new QueueSnapshotStreamReader(
            source.Reader,
            (snapshot, _) =>
            {
                applied.Add((snapshot.RootAgentSessionId, [.. snapshot.Queues.Select(static queue => queue.Clone())]));
                return Task.CompletedTask;
            });

        await source.WriteAsync(
            new SnapshotFixture(1, 0, false, new QueueState { OwnerAgentSessionId = "child", Name = "work", Description = "child queue", ItemCount = 1 }).Event,
            cancellationToken);
        await source.WriteAsync(
            new SnapshotFixture(1, 1, true, new QueueState { OwnerAgentSessionId = "root", Name = "work", Description = "root queue", ItemCount = 2 }).Event,
            cancellationToken);
        await source.WriteAsync(
            new Event { Id = "visible", TextChunk = new TextChunk { Fragment = "answer" } },
            cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("visible");
        _ = await Assert.That(applied).Count().IsEqualTo(1);
        _ = await Assert.That(applied[0].RootAgentSessionId).IsEqualTo("root");
        _ = await Assert.That(string.Join(',', applied[0].Queues.Select(static queue =>
            $"{queue.OwnerAgentSessionId}:{queue.Name}:{queue.ItemCount}")))
            .IsEqualTo("child:work:1,root:work:2");

        await source.WriteAsync(new SnapshotFixture(1, 0, true, new QueueState { OwnerAgentSessionId = "ignored", Name = "stale", Description = "ignored queue", ItemCount = 1 }).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture(3, 1, true, new QueueState { OwnerAgentSessionId = "ignored", Name = "orphan", Description = "ignored queue", ItemCount = 1 }).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture(3, 0, false, new QueueState { OwnerAgentSessionId = "child", Name = "newer", Description = "child queue", ItemCount = 3 }).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture(2, 0, true).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture(3, 1, true, new QueueState { OwnerAgentSessionId = "root", Name = "latest", Description = "root queue", ItemCount = 4 }).Event, cancellationToken);
        await source.WriteAsync(
            new Event { Id = "next", TextChunk = new TextChunk { Fragment = "next" } },
            cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("next");
        _ = await Assert.That(applied).Count().IsEqualTo(2);
        _ = await Assert.That(string.Join(',', applied[1].Queues.Select(static queue =>
            $"{queue.OwnerAgentSessionId}:{queue.Name}:{queue.ItemCount}")))
            .IsEqualTo("child:newer:3,root:latest:4");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Partitions_interleaved_owners_and_rejects_stale_or_removed_generations(
        bool removed,
        CancellationToken cancellationToken)
    {
        var source = new ChannelStreamWriter<Event>();
        var applied = new List<QueueSnapshot>();
        var reader = new QueueSnapshotStreamReader(source.Reader, (snapshot, _) =>
        {
            applied.Add(snapshot.Clone());
            return Task.CompletedTask;
        });
        var root = new QueueSnapshot
        {
            OwnerAgentSessionId = "root",
            InventoryInstanceId = "root-one",
            Revision = 8,
            FinalChunk = false,
            Queues = { new QueueState { Name = "root-queue", OwnerAgentSessionId = "root", ItemCount = 1 } },
        };
        var child = root.Clone();
        child.OwnerAgentSessionId = "child";
        child.InventoryInstanceId = "child-one";
        child.Revision = 1;
        child.Queues.Clear();
        await source.WriteAsync(new Event { QueueSnapshot = root.Clone() }, cancellationToken);
        await source.WriteAsync(new Event { QueueSnapshot = child.Clone() }, cancellationToken);
        root.ChunkIndex = 1;
        root.FinalChunk = true;
        await source.WriteAsync(new Event { QueueSnapshot = root.Clone() }, cancellationToken);
        child.ChunkIndex = 1;
        child.FinalChunk = true;
        await source.WriteAsync(new Event { QueueSnapshot = child.Clone() }, cancellationToken);
        child.ChunkIndex = 0;
        child.Revision = 2;
        child.Removed = removed;
        await source.WriteAsync(new Event { QueueSnapshot = child.Clone() }, cancellationToken);
        child.Revision = 1;
        child.Removed = false;
        await source.WriteAsync(new Event { QueueSnapshot = child.Clone() }, cancellationToken);
        root.ChunkIndex = 0;
        root.FinalChunk = false;
        root.InventoryInstanceId = "root-two";
        root.Revision = 0;
        await source.WriteAsync(new Event { QueueSnapshot = root.Clone() }, cancellationToken);
        var stale = root.Clone();
        stale.InventoryInstanceId = "root-one";
        stale.Revision = 99;
        stale.FinalChunk = true;
        await source.WriteAsync(new Event { QueueSnapshot = stale }, cancellationToken);
        root.ChunkIndex = 1;
        root.FinalChunk = true;
        await source.WriteAsync(new Event { QueueSnapshot = root.Clone() }, cancellationToken);
        await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk() }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(string.Join(',', applied.Select(static snapshot =>
            $"{snapshot.OwnerAgentSessionId}:{snapshot.InventoryInstanceId}:{snapshot.Revision}")))
            .IsEqualTo("root:root-one:8,child:child-one:1,child:child-one:2,root:root-two:0");
        _ = await Assert.That(applied[2].Removed).IsEqualTo(removed);
        _ = await Assert.That(applied[0].Queues.Count).IsEqualTo(2);

        var reconnected = new QueueSnapshotStreamReader(source.Reader, (snapshot, _) =>
        {
            applied.Add(snapshot.Clone());
            return Task.CompletedTask;
        });
        root.ChunkIndex = 0;
        root.FinalChunk = true;
        await source.WriteAsync(new Event { QueueSnapshot = root.Clone() }, cancellationToken);
        await source.WriteAsync(new Event { Id = "reconnected", TextChunk = new TextChunk() }, cancellationToken);
        _ = await Assert.That(await reconnected.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(applied.Count).IsEqualTo(5);
        _ = await Assert.That(applied[^1].InventoryInstanceId).IsEqualTo("root-two");
    }

    private sealed class SnapshotFixture
    {
        public SnapshotFixture(ulong revision, uint chunkIndex, bool finalChunk, params QueueState[] queues)
        {
            var snapshot = new QueueSnapshot
            {
                OwnerAgentSessionId = "root",
                InventoryInstanceId = "inventory",
                Revision = revision,
                ChunkIndex = chunkIndex,
                FinalChunk = finalChunk,
                RootAgentSessionId = "root",
            };
            snapshot.Queues.AddRange(queues);
            Event = new Event { QueueSnapshot = snapshot };
        }

        public Event Event { get; }
    }
}

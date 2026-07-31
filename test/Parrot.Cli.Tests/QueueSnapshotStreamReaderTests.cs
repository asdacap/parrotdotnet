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
            (rootAgentSessionId, queues, _) =>
            {
                applied.Add((rootAgentSessionId, [.. queues.Select(static queue => queue.Clone())]));
                return Task.CompletedTask;
            });

        await source.WriteAsync(
            Snapshot(1, 0, false, Queue("child", "work", 1)),
            cancellationToken);
        await source.WriteAsync(
            Snapshot(1, 1, true, Queue("root", "work", 2)),
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

        await source.WriteAsync(Snapshot(1, 0, true, Queue("ignored", "stale", 1)), cancellationToken);
        await source.WriteAsync(Snapshot(3, 1, true, Queue("ignored", "orphan", 1)), cancellationToken);
        await source.WriteAsync(Snapshot(3, 0, false, Queue("child", "newer", 3)), cancellationToken);
        await source.WriteAsync(Snapshot(2, 0, true), cancellationToken);
        await source.WriteAsync(Snapshot(3, 1, true, Queue("root", "latest", 4)), cancellationToken);
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

    private static QueueState Queue(string ownerAgentSessionId, string name, int itemCount) => new()
    {
        OwnerAgentSessionId = ownerAgentSessionId,
        Name = name,
        Description = $"{ownerAgentSessionId} queue",
        ItemCount = itemCount,
    };

    private static Event Snapshot(
        ulong revision,
        uint chunkIndex,
        bool finalChunk,
        params QueueState[] queues)
    {
        var snapshot = new QueueSnapshot
        {
            Revision = revision,
            ChunkIndex = chunkIndex,
            FinalChunk = finalChunk,
            RootAgentSessionId = "root",
        };
        snapshot.Queues.AddRange(queues);
        return new Event { QueueSnapshot = snapshot };
    }
}

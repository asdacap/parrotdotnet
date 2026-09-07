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

    private sealed class SnapshotFixture
    {
        public SnapshotFixture(ulong revision, uint chunkIndex, bool finalChunk, params QueueState[] queues)
        {
            var snapshot = new QueueSnapshot
            {
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

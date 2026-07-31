using Parrot.Cli.Enhanced;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class ShellProcessSnapshotStreamReaderTests
{
    [Test]
    public async Task Applies_only_complete_ordered_new_snapshots_and_bypasses_activity_stream(
        CancellationToken cancellationToken)
    {
        var source = new ChannelStreamWriter<Event>();
        var applied = new List<ShellProcessSnapshot>();
        var reader = new ShellProcessSnapshotStreamReader(
            source.Reader,
            (snapshot, _) =>
            {
                applied.Add(snapshot.Clone());
                return Task.CompletedTask;
            });

        await source.WriteAsync(Snapshot("one", 1, 0, 2, Process("first")), cancellationToken);
        await source.WriteAsync(Snapshot("one", 1, 1, 2, Process("second")), cancellationToken);
        await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk { Fragment = "answer" } }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("visible");
        _ = await Assert.That(applied).Count().IsEqualTo(1);
        _ = await Assert.That(string.Join(',', applied[0].Processes.Select(static process => process.ProcessId)))
            .IsEqualTo("first,second");

        await source.WriteAsync(Snapshot("one", 1, 0, 1, Process("stale")), cancellationToken);
        await source.WriteAsync(Snapshot("one", 3, 1, 2, Process("orphan")), cancellationToken);
        await source.WriteAsync(Snapshot("one", 2, 0, 1), cancellationToken);
        await source.WriteAsync(new Event { Id = "next", TextChunk = new TextChunk { Fragment = "next" } }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("next");
        _ = await Assert.That(applied).Count().IsEqualTo(2);
        _ = await Assert.That(applied[1].Revision).IsEqualTo(2UL);
        _ = await Assert.That(applied[1].Processes).IsEmpty();
    }

    [Test]
    public async Task Rejects_delayed_chunks_from_a_retired_inventory_instance(CancellationToken cancellationToken)
    {
        var source = new ChannelStreamWriter<Event>();
        var applied = new List<ShellProcessSnapshot>();
        var reader = new ShellProcessSnapshotStreamReader(
            source.Reader,
            (snapshot, _) =>
            {
                applied.Add(snapshot.Clone());
                return Task.CompletedTask;
            });

        await source.WriteAsync(Snapshot("old", 4, 0, 1, Process("old")), cancellationToken);
        await source.WriteAsync(Snapshot("new", 1, 0, 1, Process("new")), cancellationToken);
        await source.WriteAsync(Snapshot("old", 5, 0, 1, Process("resurrected")), cancellationToken);
        await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk() }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(applied).Count().IsEqualTo(2);
        _ = await Assert.That(applied[^1].InventoryInstanceId).IsEqualTo("new");
        _ = await Assert.That(applied[^1].Processes[0].ProcessId).IsEqualTo("new");
    }

    private static ActiveShellProcess Process(string id) => new()
    {
        ProcessId = id,
        Name = id,
        Command = "sleep 10",
    };

    private static Event Snapshot(
        string instanceId,
        ulong revision,
        uint chunkIndex,
        uint chunkCount,
        params ActiveShellProcess[] processes)
    {
        var snapshot = new ShellProcessSnapshot
        {
            InventoryInstanceId = instanceId,
            Revision = revision,
            ChunkIndex = chunkIndex,
            ChunkCount = chunkCount,
        };
        snapshot.Processes.AddRange(processes);
        return new Event { ShellProcessSnapshot = snapshot };
    }
}

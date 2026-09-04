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

        await source.WriteAsync(Snapshot("one", 1, 0, 2, [Process("first")], []), cancellationToken);
        await source.WriteAsync(Snapshot("one", 1, 1, 2, [Process("second")], [Completion("done", 7_123)]), cancellationToken);
        await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk { Fragment = "answer" } }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("visible");
        _ = await Assert.That(applied).Count().IsEqualTo(1);
        _ = await Assert.That(string.Join(',', applied[0].Processes.Select(static process => process.ProcessId)))
            .IsEqualTo("first,second");
        _ = await Assert.That(applied[0].CompletedProcesses).HasSingleItem();
        _ = await Assert.That(applied[0].CompletedProcesses[0].ProcessId).IsEqualTo("done");
        _ = await Assert.That(applied[0].CompletedProcesses[0].ElapsedMs).IsEqualTo(7_123L);

        await source.WriteAsync(Snapshot("one", 1, 0, 1, [Process("stale")], []), cancellationToken);
        await source.WriteAsync(Snapshot("one", 3, 1, 2, [Process("orphan")], [Completion("orphan", null)]), cancellationToken);
        await source.WriteAsync(Snapshot("one", 2, 0, 1, [], [Completion("done", 7_123)]), cancellationToken);
        await source.WriteAsync(new Event { Id = "next", TextChunk = new TextChunk { Fragment = "next" } }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("next");
        _ = await Assert.That(applied).Count().IsEqualTo(2);
        _ = await Assert.That(applied[1].Revision).IsEqualTo(2UL);
        _ = await Assert.That(applied[1].Processes).IsEmpty();
        _ = await Assert.That(applied[1].CompletedProcesses).HasSingleItem();
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

        await source.WriteAsync(Snapshot("old", 4, 0, 1, [Process("old")], []), cancellationToken);
        await source.WriteAsync(Snapshot("new", 1, 0, 1, [Process("new")], []), cancellationToken);
        await source.WriteAsync(Snapshot("old", 5, 0, 1, [Process("resurrected")], [Completion("old", 9_000)]), cancellationToken);
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

    private static CompletedShellProcess Completion(string id, long? elapsedMilliseconds)
    {
        var completion = new CompletedShellProcess { ProcessId = id };
        if (elapsedMilliseconds is { } value)
        {
            completion.ElapsedMs = value;
        }

        return completion;
    }

    private static Event Snapshot(
        string instanceId,
        ulong revision,
        uint chunkIndex,
        uint chunkCount,
        IEnumerable<ActiveShellProcess> processes,
        IEnumerable<CompletedShellProcess> completedProcesses)
    {
        var snapshot = new ShellProcessSnapshot
        {
            InventoryInstanceId = instanceId,
            Revision = revision,
            ChunkIndex = chunkIndex,
            ChunkCount = chunkCount,
        };
        snapshot.Processes.AddRange(processes);
        snapshot.CompletedProcesses.AddRange(completedProcesses);
        return new Event { ShellProcessSnapshot = snapshot };
    }
}

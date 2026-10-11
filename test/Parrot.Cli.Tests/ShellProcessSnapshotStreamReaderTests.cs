using Parrot.Cli.Enhanced;
using Parrot.Core.Tests;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class ShellProcessSnapshotStreamReaderTests
{
    [Test]
    public async Task Applies_only_complete_ordered_new_snapshots_and_bypasses_activity_stream(
        CancellationToken cancellationToken)
    {
        var source = new ChannelStreamWriter<Event>();
        var applied = new List<(ToolSnapshot Inventory, ShellProcessSnapshot Snapshot)>();
        var reader = new ShellProcessSnapshotStreamReader(
            source.Reader,
            (inventory, snapshot, _) =>
            {
                applied.Add((inventory.Clone(), snapshot.Clone()));
                return Task.CompletedTask;
            });

        await source.WriteAsync(new SnapshotFixture("one", 1, 0, 2, [new ActiveShellProcess { ProcessId = "first", Name = "first", Command = "sleep 10" }], []).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture("one", 1, 1, 2, [new ActiveShellProcess { ProcessId = "second", Name = "second", Command = "sleep 10" }], [new CompletedShellProcess { ProcessId = "done", ElapsedMs = 7_123 }]).Event, cancellationToken);
        await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk { Fragment = "answer" } }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("visible");
        _ = await Assert.That(applied).Count().IsEqualTo(1);
        _ = await Assert.That(string.Join(',', applied[0].Snapshot.Processes.Select(static process => process.ProcessId)))
            .IsEqualTo("first,second");
        _ = await Assert.That(applied[0].Snapshot.CompletedProcesses).HasSingleItem();
        _ = await Assert.That(applied[0].Snapshot.CompletedProcesses[0].ProcessId).IsEqualTo("done");
        _ = await Assert.That(applied[0].Snapshot.CompletedProcesses[0].ElapsedMs).IsEqualTo(7_123L);

        await source.WriteAsync(new SnapshotFixture("one", 1, 0, 1, [new ActiveShellProcess { ProcessId = "stale", Name = "stale", Command = "sleep 10" }], []).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture("one", 3, 1, 2, [new ActiveShellProcess { ProcessId = "orphan", Name = "orphan", Command = "sleep 10" }], [new CompletedShellProcess { ProcessId = "orphan" }]).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture("one", 2, 0, 1, [], [new CompletedShellProcess { ProcessId = "done", ElapsedMs = 7_123 }]).Event, cancellationToken);
        await source.WriteAsync(new Event { Id = "next", TextChunk = new TextChunk { Fragment = "next" } }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(reader.Current.Id).IsEqualTo("next");
        _ = await Assert.That(applied).Count().IsEqualTo(2);
        _ = await Assert.That(applied[1].Inventory.Revision).IsEqualTo(2UL);
        _ = await Assert.That(applied[1].Snapshot.Processes).IsEmpty();
        _ = await Assert.That(applied[1].Snapshot.CompletedProcesses).HasSingleItem();
    }

    [Test]
    public async Task Rejects_delayed_chunks_from_a_retired_inventory_instance(CancellationToken cancellationToken)
    {
        var source = new ChannelStreamWriter<Event>();
        var applied = new List<(ToolSnapshot Inventory, ShellProcessSnapshot Snapshot)>();
        var reader = new ShellProcessSnapshotStreamReader(
            source.Reader,
            (inventory, snapshot, _) =>
            {
                applied.Add((inventory.Clone(), snapshot.Clone()));
                return Task.CompletedTask;
            });

        await source.WriteAsync(new SnapshotFixture("old", 4, 0, 1, [new ActiveShellProcess { ProcessId = "old", Name = "old", Command = "sleep 10" }], []).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture("new", 1, 0, 1, [new ActiveShellProcess { ProcessId = "new", Name = "new", Command = "sleep 10" }], []).Event, cancellationToken);
        await source.WriteAsync(new SnapshotFixture("old", 5, 0, 1, [new ActiveShellProcess { ProcessId = "resurrected", Name = "resurrected", Command = "sleep 10" }], [new CompletedShellProcess { ProcessId = "old", ElapsedMs = 9_000 }]).Event, cancellationToken);
        await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk() }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(applied).Count().IsEqualTo(2);
        _ = await Assert.That(applied[^1].Inventory.InventoryInstanceId).IsEqualTo("new");
        _ = await Assert.That(applied[^1].Snapshot.Processes[0].ProcessId).IsEqualTo("new");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Partitions_interleaved_owners_and_rejects_stale_or_removed_generations(
        bool removed,
        CancellationToken cancellationToken)
    {
        var source = new ChannelStreamWriter<Event>();
        var applied = new List<(ToolSnapshot Inventory, ShellProcessSnapshot Snapshot)>();
        var reader = new ShellProcessSnapshotStreamReader(source.Reader, (inventory, snapshot, _) =>
        {
            applied.Add((inventory.Clone(), snapshot.Clone()));
            return Task.CompletedTask;
        });
        var root = TestToolSnapshots.ProcessEnvelope(
            new ToolSnapshot { OwnerAgentSessionId = "root", InventoryInstanceId = "root-one", Revision = 8, FinalChunk = false },
            new ShellProcessSnapshot { Processes = { new ActiveShellProcess { ProcessId = "root-process", OwnerAgentSessionId = "root" } } });
        var child = TestToolSnapshots.ProcessEnvelope(
            new ToolSnapshot { OwnerAgentSessionId = "child", InventoryInstanceId = "child-one", Revision = 1, FinalChunk = false },
            new ShellProcessSnapshot());
        await source.WriteAsync(new Event { ToolSnapshot = root.Clone() }, cancellationToken);
        await source.WriteAsync(new Event { ToolSnapshot = child.Clone() }, cancellationToken);
        root.ChunkIndex = 1;
        root.FinalChunk = true;
        await source.WriteAsync(new Event { ToolSnapshot = root.Clone() }, cancellationToken);
        child.ChunkIndex = 1;
        child.FinalChunk = true;
        await source.WriteAsync(new Event { ToolSnapshot = child.Clone() }, cancellationToken);
        child.ChunkIndex = 0;
        child.Revision = 2;
        child.Removed = removed;
        await source.WriteAsync(new Event { ToolSnapshot = child.Clone() }, cancellationToken);
        child.Revision = 1;
        child.Removed = false;
        await source.WriteAsync(new Event { ToolSnapshot = child.Clone() }, cancellationToken);
        root.ChunkIndex = 0;
        root.FinalChunk = false;
        root.InventoryInstanceId = "root-two";
        root.Revision = 0;
        await source.WriteAsync(new Event { ToolSnapshot = root.Clone() }, cancellationToken);
        var stale = root.Clone();
        stale.InventoryInstanceId = "root-one";
        stale.Revision = 99;
        stale.FinalChunk = true;
        await source.WriteAsync(new Event { ToolSnapshot = stale }, cancellationToken);
        root.ChunkIndex = 1;
        root.FinalChunk = true;
        await source.WriteAsync(new Event { ToolSnapshot = root.Clone() }, cancellationToken);
        await source.WriteAsync(new Event { Id = "visible", TextChunk = new TextChunk() }, cancellationToken);

        _ = await Assert.That(await reader.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(string.Join(',', applied.Select(static item =>
            $"{item.Inventory.OwnerAgentSessionId}:{item.Inventory.InventoryInstanceId}:{item.Inventory.Revision}")))
            .IsEqualTo("root:root-one:8,child:child-one:1,child:child-one:2,root:root-two:0");
        _ = await Assert.That(applied[2].Inventory.Removed).IsEqualTo(removed);
        _ = await Assert.That(applied[0].Snapshot.Processes.Count).IsEqualTo(2);

        var reconnected = new ShellProcessSnapshotStreamReader(source.Reader, (inventory, snapshot, _) =>
        {
            applied.Add((inventory.Clone(), snapshot.Clone()));
            return Task.CompletedTask;
        });
        root.ChunkIndex = 0;
        await source.WriteAsync(new Event { ToolSnapshot = root.Clone() }, cancellationToken);
        await source.WriteAsync(new Event { Id = "reconnected", TextChunk = new TextChunk() }, cancellationToken);
        _ = await Assert.That(await reconnected.MoveNext(cancellationToken)).IsTrue();
        _ = await Assert.That(applied.Count).IsEqualTo(5);
        _ = await Assert.That(applied[^1].Inventory.InventoryInstanceId).IsEqualTo("root-two");
    }

    private sealed class SnapshotFixture
    {
        public SnapshotFixture(
            string instanceId,
            ulong revision,
            uint chunkIndex,
            uint chunkCount,
            IEnumerable<ActiveShellProcess> processes,
            IEnumerable<CompletedShellProcess> completedProcesses)
        {
            var snapshot = new ShellProcessSnapshot();
            snapshot.Processes.AddRange(processes);
            snapshot.CompletedProcesses.AddRange(completedProcesses);
            Event = TestToolSnapshots.Process(
                new ToolSnapshot
                {
                    OwnerAgentSessionId = "root",
                    InventoryInstanceId = instanceId,
                    Revision = revision,
                    ChunkIndex = chunkIndex,
                    FinalChunk = chunkIndex + 1 == chunkCount,
                },
                snapshot);
        }

        public Event Event { get; }
    }
}

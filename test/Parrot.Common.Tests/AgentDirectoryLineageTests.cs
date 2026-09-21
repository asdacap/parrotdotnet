using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentDirectoryLineageTests
{
    [Test]
    public async Task Resolves_directories_from_the_root_and_orders_occupants_by_start()
    {
        var lineage = AgentDirectoryLineage.Resolve(
        [
            new("main", string.Empty, "main"),
            new("worker-1", "main", "worker"),
            new("leaf-1", "worker-1", "leaf"),
            new("orphan", "missing-parent", "orphan"),
            new("worker-2", "main", "worker"),
            new("leaf-2", "worker-2", "leaf"),
            new("other-root", string.Empty, "worker"),
        ]);

        var directories = lineage.Directories
            .Select(static directory => $"{string.Join('/', directory.NamePath)}={string.Join(',', directory.SessionIds)}")
            .Order(StringComparer.Ordinal);
        _ = await Assert.That(string.Join(' ', directories))
            .IsEqualTo("main/worker/leaf=leaf-1,leaf-2 main/worker=worker-1,worker-2 main=main worker=other-root");
        _ = await Assert.That(string.Join(',', lineage.Unresolved)).IsEqualTo("orphan");
        _ = await Assert.That(lineage.Contains("leaf-2")).IsTrue();
        _ = await Assert.That(lineage.Contains("orphan")).IsFalse();
        _ = await Assert.That(string.Join(',', lineage.OccupantsOf(["main", "worker"], "worker-3"))).IsEqualTo("worker-1,worker-2,worker-3");
        _ = await Assert.That(string.Join(',', lineage.OccupantsOf(["main", "worker"], "worker-2"))).IsEqualTo("worker-1,worker-2");
        _ = await Assert.That(string.Join(',', lineage.OccupantsOf(["main", "fresh"], "fresh-1"))).IsEqualTo("fresh-1");
    }
}

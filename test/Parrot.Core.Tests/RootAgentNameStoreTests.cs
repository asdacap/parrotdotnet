using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class RootAgentNameStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Allocates_lowest_names_and_reuses_a_released_unpublished_gap()
    {
        var store = CreateStore();
        var first = store.Reserve("one", string.Empty);
        var second = store.Reserve("two", string.Empty);
        var third = store.Reserve("three", string.Empty);

        _ = await Assert.That(string.Join(',', first.RootAgentName, second.RootAgentName, third.RootAgentName))
            .IsEqualTo("main,main-2,main-3");

        store.Release(second);
        var replacement = store.Reserve("four", string.Empty);

        _ = await Assert.That(replacement.RootAgentName).IsEqualTo("main-2");
    }

    [Test]
    public async Task Retains_own_exact_name_and_skips_published_live_and_foreign_owners()
    {
        var store = CreateStore();
        var main = store.Reserve("one", string.Empty);
        var retained = store.Reserve("one", main.RootAgentName);
        new SessionIndex(_root).Publish(new SessionMeta
        {
            Id = "published",
            RootAgentName = "main-2",
            WorkingDirectory = "/work",
            HostKey = "host",
            ProviderId = "provider",
            Model = "model",
        });
        var live = new RootAgentNameStore(_root, "host", 11, static _ => true, new SessionIndex(_root));
        _ = live.Reserve("live", string.Empty);
        var foreign = new RootAgentNameStore(_root, "foreign", 12, static _ => false, new SessionIndex(_root));
        _ = foreign.Reserve("foreign", string.Empty);

        _ = await Assert.That(retained.RootAgentName).IsEqualTo("main");
        _ = await Assert.That(retained.Acquired).IsFalse();
        _ = await Assert.That(store.Reserve("next", string.Empty).RootAgentName).IsEqualTo("main-5");
    }

    [Test]
    public async Task Reclaims_dead_local_owner_but_does_not_release_a_stale_token()
    {
        var dead = new RootAgentNameStore(_root, "host", 11, static _ => false, new SessionIndex(_root));
        var first = dead.Reserve("dead", string.Empty);
        var reclaimer = new RootAgentNameStore(
            _root,
            "host",
            12,
            static processId => processId == 12,
            new SessionIndex(_root));
        var replacement = reclaimer.Reserve("replacement", string.Empty);

        dead.Release(first);

        _ = await Assert.That(replacement.RootAgentName).IsEqualTo("main");
        _ = await Assert.That(reclaimer.Reserve("next", string.Empty).RootAgentName).IsEqualTo("main-2");
    }

    [Test]
    public async Task Concurrent_independent_stores_allocate_every_name_once()
    {
        var names = await Task.WhenAll(Enumerable.Range(1, 12).Select(index => Task.Run(() =>
            CreateStore().Reserve($"session-{index}", string.Empty).RootAgentName)));

        _ = await Assert.That(string.Join(',', names.Order(StringComparer.Ordinal)))
            .IsEqualTo("main,main-10,main-11,main-12,main-2,main-3,main-4,main-5,main-6,main-7,main-8,main-9");
    }

    private RootAgentNameStore CreateStore() =>
        new(_root, "host", Environment.ProcessId, static _ => true, new SessionIndex(_root));
}

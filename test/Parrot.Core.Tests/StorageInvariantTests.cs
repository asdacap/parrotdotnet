using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Core.Tests;

// These are the M2 invariants that fail silently rather than loudly, so they
// are asserted rather than trusted.
internal sealed class StorageInvariantTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Journal_mode_is_truncate_and_no_shm_or_wal_appears()
    {
        var path = Path.Combine(_root, "sessions", "one", "session.db");

        using (var database = SessionDatabase.Open(path))
        {
            var repository = new EventRepository(database);

            for (var index = 0; index < 50; index++)
            {
                repository.Append(new Event { Id = $"{index}", AgentSessionId = "agent" }, "user", "hello");
            }

            // WAL is the failure this guards: it coordinates through a
            // memory-mapped -shm file, and two hosts mapping one file over a
            // network filesystem get incoherent private views.
            _ = await Assert.That(database.JournalMode()).IsEqualTo("truncate");
        }

        var strays = Directory.EnumerateFiles(Path.GetDirectoryName(path) ?? ".")
            .Where(file => file.EndsWith("-shm", StringComparison.Ordinal)
                || file.EndsWith("-wal", StringComparison.Ordinal))
            .ToList();

        _ = await Assert.That(strays).IsEmpty();
    }

    [Test]
    public async Task An_event_and_its_projection_commit_together()
    {
        using var database = SessionDatabase.Open(Path.Combine(_root, "sessions", "two", "session.db"));
        var repository = new EventRepository(database);

        repository.Append(new Event { Id = "e1", AgentSessionId = "agent" }, "user", "the prompt");
        repository.Append(new Event { Id = "e2", AgentSessionId = "agent" }, null, null);

        _ = await Assert.That(repository.Replay().Count).IsEqualTo(2);

        // The event with no projection contributes no message, and the one with
        // a projection contributes exactly one.
        _ = await Assert.That(repository.Messages("agent")).Count().IsEqualTo(1);
        _ = await Assert.That(repository.Messages("agent")[0]).Contains("the prompt");
    }

    [Test]
    public async Task Events_survive_reopening_the_database()
    {
        var path = Path.Combine(_root, "sessions", "three", "session.db");

        using (var first = SessionDatabase.Open(path))
        {
            new EventRepository(first).Append(new Event { Id = "kept", AgentSessionId = "agent" }, "user", "durable");
        }

        using var second = SessionDatabase.Open(path);

        _ = await Assert.That(new EventRepository(second).Replay()[0].Id).IsEqualTo("kept");
    }

    [Test]
    public async Task A_live_binding_is_not_stolen_and_an_abandoned_one_is_reclaimed()
    {
        var claim = new WorkingDirectoryClaim(_root, "host");

        var first = claim.Claim("/work", "session-a", static _ => false);
        _ = await Assert.That(first.Disposition).IsEqualTo(ClaimDisposition.Fresh);

        // The owning process is alive, so the binding stands and the caller is
        // told to take a session of its own.
        var live = claim.Claim("/work", "session-b", static _ => true);
        _ = await Assert.That(live.Disposition).IsEqualTo(ClaimDisposition.Live);
        _ = await Assert.That(live.SessionId).IsEqualTo("session-a");

        // The owning process is gone, so the binding is abandoned.
        var reclaimed = claim.Claim("/work", "session-c", static _ => false);
        _ = await Assert.That(reclaimed.Disposition).IsEqualTo(ClaimDisposition.Reclaimed);

        // Reclaiming resumes the session the binding named, rather than
        // starting a fresh conversation every time a process dies.
        _ = await Assert.That(reclaimed.SessionId).IsEqualTo("session-a");
    }

    [Test]
    public async Task Listing_reads_meta_json_and_never_a_database()
    {
        var index = new SessionIndex(_root);

        _ = await Assert.That(index.BlobDirectoryFor("listed"))
            .IsEqualTo(Path.Combine(index.DirectoryFor("listed"), "blob"));

        index.Publish(new SessionMeta
        {
            Id = "listed",
            WorkingDirectory = "/work",
            HostKey = "host",
            ProviderId = "opencode-go",
            Model = "deepseek-v4-pro",
        });

        var listed = index.List();

        _ = await Assert.That(listed).Count().IsEqualTo(1);
        _ = await Assert.That(listed[0].Id).IsEqualTo("listed");

        // Published by rename, so a reader never sees it half-written.
        var staging = Directory.EnumerateFiles(index.DirectoryFor("listed"), "*.staging").ToList();
        _ = await Assert.That(staging).IsEmpty();
    }

    [Test]
    public async Task Different_working_directories_get_different_bindings()
    {
        _ = await Assert.That(WorkingDirectoryClaim.Fingerprint("/a"))
            .IsNotEqualTo(WorkingDirectoryClaim.Fingerprint("/b"));

        _ = await Assert.That(WorkingDirectoryClaim.Fingerprint("/a"))
            .IsEqualTo(WorkingDirectoryClaim.Fingerprint("/a"));
    }
}

using Parrot.Protocol;
using Parrot.State;
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
                _ = repository.Append(new Event { Id = $"{index}", AgentSessionId = "agent" }, "user", "hello");
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

        _ = repository.Append(new Event { Id = "e1", AgentSessionId = "agent" }, "user", "the prompt");
        _ = repository.Append(new Event { Id = "e2", AgentSessionId = "agent" }, null, null);

        _ = await Assert.That(repository.Replay().Count).IsEqualTo(2);

        // The event with no projection contributes no message, and the one with
        // a projection contributes exactly one.
        _ = await Assert.That(repository.Messages("agent")).Count().IsEqualTo(1);
        _ = await Assert.That(repository.Messages("agent")[0]).Contains("the prompt");
    }

    [Test]
    public async Task Usage_projection_replaces_agent_totals_and_aggregates_agents()
    {
        var path = Path.Combine(_root, "sessions", "usage", "session.db");
        ulong revision;
        using (var database = SessionDatabase.Open(path))
        {
            var repository = new EventRepository(database);
            var root = repository.SessionState("user", "build").AgentSessionId;
            _ = repository.Append(
                new Event
                {
                    Id = "root-1",
                    AgentSessionId = root,
                    AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                    {
                        InputTokens = 10,
                        CachedInputTokens = 3,
                        OutputTokens = 4,
                        ContextSize = 8,
                        ContextLimit = 128,
                        InputCost = 1,
                        OutputCost = 2,
                    },
                },
                null,
                null);
            _ = repository.Append(
                new Event
                {
                    Id = "child-1",
                    AgentSessionId = "child",
                    AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                    {
                        InputTokens = 7,
                        CachedInputTokens = 2,
                        OutputTokens = 5,
                        ContextSize = 99,
                        ContextLimit = 999,
                        InputCost = 0.5,
                        OutputCost = 0.25,
                    },
                },
                null,
                null);
            var usage = repository.Append(
                new Event
                {
                    Id = "root-2",
                    AgentSessionId = root,
                    AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                    {
                        InputTokens = 15,
                        CachedInputTokens = 5,
                        OutputTokens = 6,
                        ContextSize = 9,
                        ContextLimit = 128,
                        InputCost = 1.5,
                        OutputCost = 2.5,
                    },
                },
                null,
                null)
                ?? throw new InvalidOperationException("statistics did not project usage");

            revision = usage.Revision;
            _ = await Assert.That(usage.InputTokens).IsEqualTo(22);
            _ = await Assert.That(usage.CachedInputTokens).IsEqualTo(7);
            _ = await Assert.That(usage.OutputTokens).IsEqualTo(11);
            _ = await Assert.That(usage.ContextSize).IsEqualTo(9);
            _ = await Assert.That(usage.ContextLimit).IsEqualTo(128);
            _ = await Assert.That(usage.InputCost).IsEqualTo(2.0);
            _ = await Assert.That(usage.OutputCost).IsEqualTo(2.75);
        }

        using var reopened = SessionDatabase.Open(path);
        var restored = new EventRepository(reopened).Usage();
        _ = await Assert.That(restored.Revision).IsEqualTo(revision);
        _ = await Assert.That(restored.InputTokens).IsEqualTo(22);
        _ = await Assert.That(restored.OutputTokens).IsEqualTo(11);
    }

    [Test]
    public async Task Usage_projection_backfills_latest_legacy_statistics_once()
    {
        using var database = SessionDatabase.Open(Path.Combine(_root, "sessions", "legacy-usage", "session.db"));
        var repository = new EventRepository(database);
        var root = repository.SessionState("user", "build").AgentSessionId;
        _ = repository.Append(
            new Event
            {
                Id = "old-root",
                AgentSessionId = root,
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                {
                    InputTokens = 4,
                    CachedInputTokens = 1,
                    OutputTokens = 2,
                    ContextSize = 3,
                    ContextLimit = 100,
                    InputCost = 0.4,
                    OutputCost = 0.2,
                },
            },
            null,
            null);
        _ = repository.Append(
            new Event
            {
                Id = "new-root",
                AgentSessionId = root,
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                {
                    InputTokens = 9,
                    CachedInputTokens = 2,
                    OutputTokens = 5,
                    ContextSize = 6,
                    ContextLimit = 100,
                    InputCost = 0.9,
                    OutputCost = 0.5,
                },
            },
            null,
            null);
        _ = repository.Append(
            new Event
            {
                Id = "child",
                AgentSessionId = "child",
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                {
                    InputTokens = 3,
                    CachedInputTokens = 1,
                    OutputTokens = 1,
                    ContextSize = 80,
                    ContextLimit = 200,
                    InputCost = 0.3,
                    OutputCost = 0.1,
                },
            },
            null,
            null);

        using (var legacy = database.Connection.CreateCommand())
        {
            legacy.CommandText = "DELETE FROM agent_usage; DELETE FROM projection_version;";
            _ = await legacy.ExecuteNonQueryAsync();
        }

        var backfilled = new EventRepository(database).Usage();
        _ = await Assert.That(backfilled.InputTokens).IsEqualTo(12);
        _ = await Assert.That(backfilled.CachedInputTokens).IsEqualTo(3);
        _ = await Assert.That(backfilled.OutputTokens).IsEqualTo(6);
        _ = await Assert.That(backfilled.ContextSize).IsEqualTo(6);
        _ = await Assert.That(backfilled.ContextLimit).IsEqualTo(100);
        _ = await Assert.That(backfilled.InputCost).IsEqualTo(1.2);
        _ = await Assert.That(backfilled.OutputCost).IsEqualTo(0.6);

        var second = new EventRepository(database).Usage();
        _ = await Assert.That(second).IsEqualTo(backfilled);
    }

    [Test]
    public async Task Failed_event_insert_does_not_change_usage_projection()
    {
        using var database = SessionDatabase.Open(Path.Combine(_root, "sessions", "atomic-usage", "session.db"));
        var repository = new EventRepository(database);
        var root = repository.SessionState("user", "build").AgentSessionId;
        var first = new Event
        {
            Id = "same",
            AgentSessionId = root,
            AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
            {
                InputTokens = 4,
                CachedInputTokens = 1,
                OutputTokens = 2,
                ContextSize = 3,
                ContextLimit = 100,
                InputCost = 0.4,
                OutputCost = 0.2,
            },
        };
        _ = repository.Append(first, null, null);

        _ = await Assert.That(() => repository.Append(
            new Event
            {
                Id = "same",
                AgentSessionId = root,
                AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
                {
                    InputTokens = 40,
                    CachedInputTokens = 10,
                    OutputTokens = 20,
                    ContextSize = 30,
                    ContextLimit = 100,
                    InputCost = 4,
                    OutputCost = 2,
                },
            },
            null,
            null)).ThrowsException();

        var usage = repository.Usage();
        _ = await Assert.That(usage.InputTokens).IsEqualTo(4);
        _ = await Assert.That(usage.OutputTokens).IsEqualTo(2);
    }

    [Test]
    public async Task Events_survive_reopening_the_database()
    {
        var path = Path.Combine(_root, "sessions", "three", "session.db");

        using (var first = SessionDatabase.Open(path))
        {
            _ = new EventRepository(first).Append(
                new Event { Id = "kept", AgentSessionId = "agent" },
                "user",
                "durable");
        }

        using var second = SessionDatabase.Open(path);

        _ = await Assert.That(new EventRepository(second).Replay()[0].Id).IsEqualTo("kept");
    }

    [Test]
    public async Task A_live_binding_is_not_stolen_and_an_abandoned_one_is_reclaimed()
    {
        var claim = new WorkingDirectoryClaim(_root, "host");
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;

        var first = claim.Claim(workspace, "session-a", static _ => false);
        _ = await Assert.That(first.Disposition).IsEqualTo(ClaimDisposition.Fresh);

        // The owning process is alive, so the binding stands and the caller is
        // told to take a session of its own.
        var live = claim.Claim(workspace, "session-b", static _ => true);
        _ = await Assert.That(live.Disposition).IsEqualTo(ClaimDisposition.Live);
        _ = await Assert.That(live.SessionId).IsEqualTo("session-a");

        // The owning process is gone, so the binding is abandoned.
        var reclaimed = claim.Claim(workspace, "session-c", static _ => false);
        _ = await Assert.That(reclaimed.Disposition).IsEqualTo(ClaimDisposition.Reclaimed);

        // Reclaiming resumes the session the binding named, rather than
        // starting a fresh conversation every time a process dies.
        _ = await Assert.That(reclaimed.SessionId).IsEqualTo("session-a");
    }

    [Test]
    public async Task Listing_reads_meta_json_and_never_a_database()
    {
        var workspaceDirectory = Directory.CreateDirectory(Path.Combine(_root, "work")).FullName;
        var resources = new UserSessionResources(
            new StatePaths(_root, _root, _root),
            UserSessionId.Parse("listed"),
            ProjectWorkspace.FromLaunchDirectory(workspaceDirectory));
        var index = new SessionIndex(resources);

        _ = await Assert.That(resources.AgentScratch("agent-session-test").BlobDirectory)
            .IsEqualTo(Path.Combine(resources.Root, "scratch", "agent-session-test", "blobs"));

        index.Publish(new SessionMeta
        {
            Id = "listed",
            WorkingDirectory = workspaceDirectory,
            ProviderId = "opencode-go",
            Model = "deepseek-v4-pro",
        });

        var listed = new SessionCatalog(new StatePaths(_root, _root, _root)).List();

        _ = await Assert.That(listed).Count().IsEqualTo(1);
        _ = await Assert.That(listed[0].Id.Value).IsEqualTo("listed");

        // Published by rename, so a reader never sees it half-written.
        var staging = Directory.EnumerateFiles(resources.Root, "*.staging").ToList();
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

using System.Text.Json;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class SessionStoreTests : IDisposable
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
    public async Task Resumed_and_legacy_sessions_retain_or_receive_a_durable_name()
    {
        const string namedId = "user-session-named";
        const string legacyId = "user-session-legacy";
        var namedWork = Path.Combine(_root, "named-work");
        var legacyWork = Path.Combine(_root, "legacy-work");
        var index = new SessionIndex(_root);
        index.Publish(Meta(namedId, "main", namedWork, "2026-07-27T01:00:00Z"));
        index.Publish(Meta(legacyId, string.Empty, legacyWork, "2026-07-27T02:00:00Z"));
        PublishOwner(namedWork, namedId);
        PublishOwner(legacyWork, legacyId);

        var named = Open(namedWork, out var namedStore);
        using (namedStore)
        await using (named)
        {
            _ = await Assert.That(named.Name).IsEqualTo("main");
            _ = await Assert.That(index.Find(namedId)?.CreatedAt).IsEqualTo("2026-07-27T01:00:00Z");
        }

        var legacy = Open(legacyWork, out var legacyStore);
        using (legacyStore)
        await using (legacy)
        {
            _ = await Assert.That(legacy.Name).IsEqualTo("main-2");
            _ = await Assert.That(index.Find(legacyId)?.Name).IsEqualTo("main-2");
            _ = await Assert.That(index.Find(legacyId)?.CreatedAt).IsEqualTo("2026-07-27T02:00:00Z");
        }
    }

    [Test]
    public async Task Failed_session_construction_releases_its_unpublished_name()
    {
        using (var failing = new SessionStore(
            _root,
            Path.Combine(_root, "failing-work"),
            "host",
            new ThrowingUserSessions()))
        {
            _ = await Assert.That(() => failing.Open(Model())).Throws<InvalidOperationException>();
        }

        var session = Open(Path.Combine(_root, "working"), out var store);
        using (store)
        await using (session)
        {
            _ = await Assert.That(session.Name).IsEqualTo("main");
        }
    }

    private static SessionMeta Meta(string id, string name, string workingDirectory, string createdAt) =>
        new()
        {
            Id = id,
            Name = name,
            WorkingDirectory = workingDirectory,
            HostKey = "host",
            ProviderId = "unused",
            Model = "unused/model",
            CreatedAt = createdAt,
        };

    private static ProviderModel Model()
    {
        var provider = new UnusedProvider();
        return new ProviderModel(provider, new LLMModel("model", provider.Id));
    }

    private UserSession Open(string workingDirectory, out SessionStore store)
    {
        var sessions = new DirectAgentSessions();
        store = new SessionStore(
            _root,
            workingDirectory,
            "host",
            new UserSessionFactory(sessions, new ModeRegistry(Path.Combine(_root, "plans"))));
        return store.Open(Model());
    }

    private void PublishOwner(string workingDirectory, string sessionId)
    {
        var directory = Path.Combine(_root, "owners", WorkingDirectoryClaim.Fingerprint(workingDirectory));
        _ = Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "v1.json"),
            JsonSerializer.Serialize(
                new OwnerRecord
                {
                    Version = 1,
                    SessionId = sessionId,
                    WorkingDirectory = workingDirectory,
                    HostKey = "host",
                    ProcessId = int.MaxValue,
                },
                StoreJsonContext.Default.OwnerRecord));
    }

    private sealed class ThrowingUserSessions : IUserSessionFactory
    {
        public UserSession Create(
            string id,
            string name,
            ProviderModel model,
            string mode,
            EventRepository eventRepository) =>
            throw new InvalidOperationException("construction failed");
    }
}

using System.Text.Json;
using Parrot.Agent;
using Parrot.Config;
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
    public async Task Resumed_and_legacy_sessions_retain_or_receive_a_durable_root_agent_name()
    {
        const string namedId = "user-session-named";
        const string legacyId = "user-session-legacy";
        var namedWork = Path.Combine(_root, "named-work");
        var legacyWork = Path.Combine(_root, "legacy-work");
        var index = new SessionIndex(_root);
        index.Publish(Meta(namedId, "main", namedWork, "2026-07-27T01:00:00Z"));
        index.Publish(Meta("user-session-other", "main-2", "/other-work", "2026-07-27T01:30:00Z"));
        index.Publish(Meta(legacyId, string.Empty, legacyWork, "2026-07-27T02:00:00Z"));
        PublishOwner(namedWork, namedId);
        PublishOwner(legacyWork, legacyId);

        var named = Open(namedWork, out var namedStore);
        using (namedStore)
        await using (named)
        {
            _ = await Assert.That(index.Find(namedId)?.RootAgentName).IsEqualTo("main");
            _ = await Assert.That(index.Find(namedId)?.CreatedAt).IsEqualTo("2026-07-27T01:00:00Z");
        }

        var legacy = Open(legacyWork, out var legacyStore);
        using (legacyStore)
        await using (legacy)
        {
            _ = await Assert.That(index.Find(legacyId)?.RootAgentName).IsEqualTo("main-3");
            _ = await Assert.That(index.Find(legacyId)?.CreatedAt).IsEqualTo("2026-07-27T02:00:00Z");
        }
    }

    [Test]
    public async Task Resumed_session_with_null_selector_uses_legacy_model()
    {
        const string id = "user-session-null-selector";
        var workingDirectory = Path.Combine(_root, "null-selector-work");
        var index = new SessionIndex(_root);
        index.Publish(Meta(id, "main", workingDirectory, "2026-07-27T01:00:00Z"));
        var path = Path.Combine(index.DirectoryFor(id), "meta.json");
        var serialized = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(
            path,
            serialized.Replace("\"Selector\": \"\"", "\"Selector\": null", StringComparison.Ordinal));
        _ = await Assert.That(index.Find(id)?.Selector).IsNull();
        PublishOwner(workingDirectory, id);

        var session = Open(workingDirectory, out var store);
        using (store)
        await using (session)
        {
            _ = await Assert.That(session.Model).IsEqualTo("unused/model");
            _ = await Assert.That(index.Find(id)?.Selector).IsEqualTo("unused/model");
        }
    }

    [Test]
    public async Task Failed_session_construction_does_not_consume_a_root_agent_name()
    {
        var model = Model();
        var modes = Modes();
        using (var failing = new SessionStore(
            _root,
            Path.Combine(_root, "failing-work"),
            "host",
            new ThrowingUserSessions(),
            TestModels.Route(model),
            modes))
        {
            _ = await Assert.That(() => failing.Open(TestModels.Resolve(model))).Throws<InvalidOperationException>();
        }

        var session = Open(Path.Combine(_root, "working"), out var store);
        using (store)
        await using (session)
        {
            _ = await Assert.That(store.Index.Find(session.Id)?.RootAgentName).IsEqualTo("main");
        }
    }

    private static SessionMeta Meta(string id, string rootAgentName, string workingDirectory, string createdAt) =>
        new()
        {
            Id = id,
            RootAgentName = rootAgentName,
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
        var model = Model();
        var router = TestModels.Route(model);
        sessions.Use(router);
        store = new SessionStore(
            _root,
            workingDirectory,
            "host",
            new UserSessionFactory(sessions, Modes()),
            router,
            Modes());
        return store.Open(router.Resolve(model.Selector));
    }

    private ModeRegistry Modes()
    {
        var configuration = Configuration.Load(
            Path.Combine(_root, "config.yaml"),
            Path.Combine(_root, "predefined_config.yaml"));
        return new ModeRegistry(
            Path.Combine(_root, "plans"),
            new ProfileRegistry(
                configuration.Profiles,
                configuration.SandboxRules,
                configuration.DisabledTools,
                configuration.DefaultProfile));
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
            string rootAgentName,
            ResolvedModelSelection model,
            string mode,
            EventRepository eventRepository) =>
            throw new InvalidOperationException("construction failed");
    }
}

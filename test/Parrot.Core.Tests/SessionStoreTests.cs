using System.Text.Json;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Llm;
using Parrot.State;
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
        var namedWork = Directory.CreateDirectory(Path.Combine(_root, "named-work")).FullName;
        var legacyWork = Directory.CreateDirectory(Path.Combine(_root, "legacy-work")).FullName;
        var namedIndex = Index(namedId, namedWork);
        var legacyIndex = Index(legacyId, legacyWork);
        namedIndex.Publish(Meta(namedId, "main", namedWork, "2026-07-27T01:00:00Z"));
        legacyIndex.Publish(Meta(legacyId, string.Empty, legacyWork, "2026-07-27T02:00:00Z"));
        StabilizeAdmission(namedWork, namedId);
        StabilizeAdmission(legacyWork, legacyId);
        var catalog = new SessionCatalog(Paths());
        {
            await using var named = Open(namedWork);
            _ = await Assert.That(catalog.Find(UserSessionId.Parse(namedId))?.RootAgentName).IsEqualTo("main");
            _ = await Assert.That(catalog.Find(UserSessionId.Parse(namedId))?.CreatedAt)
                .IsEqualTo("2026-07-27T01:00:00Z");
        }

        await using var legacy = Open(legacyWork);
        _ = await Assert.That(catalog.Find(UserSessionId.Parse(legacyId))?.RootAgentName).IsEqualTo("main");
        _ = await Assert.That(catalog.Find(UserSessionId.Parse(legacyId))?.CreatedAt)
            .IsEqualTo("2026-07-27T02:00:00Z");
    }

    [Test]
    public async Task Legacy_metadata_without_host_identity_resumes_and_is_republished()
    {
        const string id = "user-session-legacy-host";
        const string createdAt = "2026-07-27T04:00:00Z";
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "legacy-host-work")).FullName;
        var index = Index(id, workingDirectory);
        _ = Directory.CreateDirectory(index.Resources.Root);
        var encodedWorkingDirectory = JsonEncodedText.Encode(workingDirectory).ToString();
        var legacyMetadata = $$"""
            {
              "Id": "{{id}}",
              "WorkingDirectory": "{{encodedWorkingDirectory}}",
              "RootAgentName": "main-7",
              "ProviderId": "unused",
              "Model": "unused/model",
              "Selector": "unused/model",
              "Mode": "query",
              "ProcessId": 4312,
              "CreatedAt": "{{createdAt}}"
            }
            """;
        await File.WriteAllTextAsync(index.Resources.MetadataPath, legacyMetadata);

        var catalog = new SessionCatalog(Paths()).List().Single(entry => entry.Id.Value == id);
        _ = await Assert.That(catalog.State).IsEqualTo(SessionCatalogState.Inactive);
        StabilizeAdmission(workingDirectory, id);

        await using var session = Open(workingDirectory);
        var republished = index.Find();

        _ = await Assert.That(session.Id).IsEqualTo(id);
        _ = await Assert.That(session.Model).IsEqualTo("unused/model");
        _ = await Assert.That(republished?.RootAgentName).IsEqualTo("main-7");
        _ = await Assert.That(republished?.CreatedAt).IsEqualTo(createdAt);
        var serialized = await File.ReadAllTextAsync(index.Resources.MetadataPath);
        _ = await Assert.That(serialized).DoesNotContain("HostKey").And.DoesNotContain("ProcessId");
    }

    [Test]
    public async Task Resumed_session_with_null_selector_uses_legacy_model()
    {
        const string id = "user-session-null-selector";
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "null-selector-work")).FullName;
        var index = Index(id, workingDirectory);
        index.Publish(Meta(id, "main", workingDirectory, "2026-07-27T01:00:00Z"));
        var path = index.Resources.MetadataPath;
        var serialized = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(
            path,
            serialized.Replace("\"Selector\": \"\"", "\"Selector\": null", StringComparison.Ordinal));
        _ = await Assert.That(index.Find()?.Selector).IsNull();
        StabilizeAdmission(workingDirectory, id);

        await using var session = Open(workingDirectory);
        _ = await Assert.That(session.Model).IsEqualTo("unused/model");
        _ = await Assert.That(index.Find()?.Selector).IsEqualTo("unused/model");
    }

    [Test]
    public async Task Equivalent_launch_path_resume_preserves_stored_metadata()
    {
        const string id = "user-session-equivalent-path";
        var physical = Directory.CreateDirectory(Path.Combine(_root, "physical-work")).FullName;
        var linked = Path.Combine(_root, "linked-work");
        _ = Directory.CreateSymbolicLink(linked, physical);
        var index = Index(id, linked);
        index.Publish(new SessionMeta
        {
            Id = id,
            RootAgentName = "main-7",
            WorkingDirectory = linked,
            ProviderId = "unused",
            Model = "unused/model",
            Selector = "unused/model",
            Mode = ModeRegistry.Query,
            CreatedAt = "2026-07-27T03:00:00Z",
        });
        StabilizeAdmission(linked, id);

        await using var session = Open(physical);
        var resumed = new SessionCatalog(Paths()).Find(UserSessionId.Parse(id));

        _ = await Assert.That(session.Id).IsEqualTo(id);
        _ = await Assert.That(resumed?.RootAgentName).IsEqualTo("main-7");
        _ = await Assert.That(session.Model).IsEqualTo("unused/model");
        _ = await Assert.That(session.Resources.Workspace.LaunchDirectory).IsEqualTo(physical);
        _ = await Assert.That(resumed?.WorkingDirectory).IsEqualTo(linked);
        _ = await Assert.That(resumed?.CreatedAt).IsEqualTo("2026-07-27T03:00:00Z");
    }

    [Test]
    public async Task Failed_session_construction_releases_admission_for_retry()
    {
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "working")).FullName;
        var model = Model();
        var modes = Modes();
        var failing = new SessionStore(
            Paths(),
            workingDirectory,
            "host",
            new ThrowingUserSessions(),
            TestModels.Route(model),
            modes);

        _ = await Assert.That(() => failing.Open(TestModels.Resolve(model))).Throws<InvalidOperationException>();

        await using var session = Open(workingDirectory);
        _ = await Assert.That(new SessionIndex(session.Resources).Find()?.RootAgentName).IsEqualTo("main");
    }

    private static SessionMeta Meta(string id, string rootAgentName, string workingDirectory, string createdAt) =>
        new()
        {
            Id = id,
            RootAgentName = rootAgentName,
            WorkingDirectory = workingDirectory,
            ProviderId = "unused",
            Model = "unused/model",
            CreatedAt = createdAt,
        };

    private static ProviderModel Model()
    {
        var provider = new UnusedProvider();
        return new ProviderModel(provider, new LLMModel("model", provider.Id));
    }

    private UserSession Open(string workingDirectory)
    {
        var sessions = new DirectAgentSessions();
        var model = Model();
        var router = TestModels.Route(model);
        sessions.Use(router);
        var store = new SessionStore(
            Paths(),
            workingDirectory,
            "host",
            new UserSessionFactory(sessions, Modes(), TimeSpan.FromSeconds(30)),
            router,
            Modes());
        return store.Open(router.Resolve(model.Selector));
    }

    private SessionIndex Index(string id, string workingDirectory) =>
        new(new UserSessionResources(
            Paths(),
            UserSessionId.Parse(id),
            ProjectWorkspace.FromLaunchDirectory(workingDirectory)));

    private StatePaths Paths() =>
        new(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"));

    private ModeRegistry Modes()
    {
        var paths = Paths();
        var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
        return new ModeRegistry(
            new ProfileRegistry(
                configuration.Profiles,
                configuration.SandboxRules,
                configuration.DisabledTools,
                configuration.DefaultProfile));
    }

    private void StabilizeAdmission(string workingDirectory, string sessionId)
    {
        var admission = new WorkingDirectoryClaim(Paths().State, "host")
            .CreateFresh(workingDirectory, UserSessionId.Parse(sessionId));
        admission.ActivationLease?.Dispose();
    }

    private sealed class ThrowingUserSessions : IUserSessionFactory
    {
        public UserSession Create(
            SessionResourceLease resources,
            string id,
            string rootAgentName,
            ResolvedModelSelection model,
            string mode,
            bool interactivePermissions) =>
            throw new InvalidOperationException("construction failed");
    }
}

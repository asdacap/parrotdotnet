using System.Text.Json;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class SessionStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    private readonly List<DiagnosticLogs> _diagnostics = [];

    public void Dispose()
    {
        foreach (var diagnostics in _diagnostics)
        {
            diagnostics.Dispose();
        }

        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Resume_rebuilds_inactive_agent_history_before_live_agents_are_constructed()
    {
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "history-work")).FullName;
        UserSessionResources resources;
        await using (var initial = await Open(workingDirectory))
        {
            resources = initial.Resources;
        }

        using (var database = SessionDatabase.Open(resources.DatabasePath))
        {
            var repository = new EventRepository(database);
            repository.AppendConversation(
                new Parrot.Protocol.Event { Id = "historical-message", AgentSessionId = "inactive-child" },
                ConversationOrigin.UserInput,
                LLMRole.User,
                [ConversationPart.TextPart("inactive durable history")],
                [],
                string.Empty);
        }

        var history = new AgentHistoryFile(resources, "inactive-child");
        _ = Directory.CreateDirectory(Path.GetDirectoryName(history.Path) ?? throw new InvalidOperationException());
        await File.WriteAllTextAsync(history.Path, "stale projection");
        await using var resumed = await Open(workingDirectory);

        _ = await Assert.That(await File.ReadAllTextAsync(history.Path)).Contains("inactive durable history").And.DoesNotContain("stale projection");
        _ = await Assert.That(resumed.Registry.FindScope("inactive-child")).IsNull();
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Startup_stages_are_visible_globally_before_initialization_returns(bool resume, bool fail)
    {
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "startup-work")).FullName;
        var paths = new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        string? resumedId = null;
        if (resume)
        {
            await using var existing = await Open(workingDirectory);
            resumedId = existing.Id;
        }

        using var diagnostics = new DiagnosticLogs(paths, "startup", TextWriter.Null, TimeProvider.System);
        var logPath = Path.Combine(paths.LogDirectory, "parrot-startup.log");
        var sessions = new DirectAgentSessions();
        ILLMProvider provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(model);
        sessions.Use(router);
        var factory = new ObservingUserSessions(
            new UserSessionFactory(sessions, Modes(), TestModels.PromptTemplates, new TestProfileFixture().Registry, SkillCatalogFactory(), TimeSpan.FromSeconds(30), TimeProvider.System, TestModels.RuntimeStatusProviders),
            logPath,
            fail);
        var store = new SessionStore(paths, workingDirectory, "host", factory, router, Modes(), diagnostics);
        if (fail)
        {
            _ = await Assert.That(async () => { _ = await OpenSession(); }).Throws<InvalidOperationException>();
        }
        else
        {
            await using var opened = await OpenSession();
            _ = await Assert.That(opened.Id).IsEqualTo(factory.SessionId);
        }

        var pending = factory.PendingLog;
        _ = await Assert.That(pending).Contains("event=\"workspace_complete\"").And.Contains("event=\"acquire_complete\"")
            .And.Contains("event=\"resources_complete\"").And.Contains("event=\"database_complete\"")
            .And.Contains("event=\"initialize_start\"").And.DoesNotContain("event=\"initialize_complete\"")
            .And.DoesNotContain("event=\"initialize_failure\"");
        var log = await File.ReadAllTextAsync(logPath);
        var initialization = log.Split('\n').Where(line => line.Contains("event=\"initialize_", StringComparison.Ordinal)).ToArray();
        _ = await Assert.That(initialization.Length).IsEqualTo(2);
        foreach (var line in initialization)
        {
            _ = await Assert.That(line).Contains($"session=\"{factory.SessionId}\"").And.Contains("correlation=\"");
        }

        _ = await Assert.That(initialization[1]).Contains(fail ? "event=\"initialize_failure\"" : "event=\"initialize_complete\"")
            .And.Contains("duration_ms=").And.Contains(fail ? "outcome=\"failed\"" : "outcome=\"success\"");
        _ = await Assert.That(log).DoesNotContain("private-construction-failure").And.DoesNotContain(workingDirectory);

        async Task<IUserSession> OpenSession() => resumedId is null
            ? await store.CreateFresh(router.Resolve(model.Selector), Modes().Default, false)
            : (await store.Resume(UserSessionId.Parse(resumedId), false)).Session;
    }

    [Test]
    public async Task Automatic_session_logs_append_on_resume_and_remain_isolated()
    {
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "logs-work")).FullName;
        string firstLog;
        string firstId;
        string initial;
        {
            await using var first = await Open(workingDirectory);
            firstLog = first.Resources.LogPath;
            firstId = first.Id;
            initial = await File.ReadAllTextAsync(firstLog);
            _ = await Assert.That(initial).Contains("event=\"start\"").And.Contains($"session=\"{firstId}\"");
        }

        {
            await using var resumed = await Open(workingDirectory);
            _ = await Assert.That(resumed.Id).IsEqualTo(firstId);
            var appended = await File.ReadAllTextAsync(firstLog);
            _ = await Assert.That(appended.StartsWith(initial, StringComparison.Ordinal)).IsTrue();
            _ = await Assert.That(appended).Contains("event=\"resume\"");
        }

        await using var separate = await Open(Directory.CreateDirectory(Path.Combine(_root, "other-work")).FullName);
        _ = await Assert.That(await File.ReadAllTextAsync(separate.Resources.LogPath)).Contains("event=\"start\"").And.DoesNotContain(firstId);
        _ = await Assert.That(await File.ReadAllTextAsync(firstLog)).DoesNotContain(separate.Id);
    }

    [Test]
    public async Task Failed_metadata_publication_disposes_constructed_session_before_retry()
    {
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "publication-failure")).FullName;
        var paths = new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        var sessions = new DirectAgentSessions();
        ILLMProvider provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(model);
        sessions.Use(router);
        var factory = new PublicationFailureUserSessions(new UserSessionFactory(
            sessions,
            Modes(),
            TestModels.PromptTemplates,
            new TestProfileFixture().Registry,
            SkillCatalogFactory(),
            TimeSpan.FromSeconds(30),
            TimeProvider.System,
            TestModels.RuntimeStatusProviders));
        var store = new SessionStore(paths, workingDirectory, "host", factory, router, Modes(), Diagnostics());

        _ = await Assert.That(async () =>
        {
            _ = await store.Open(TestModels.Resolve(model));
        }).Throws<IOException>();
        var constructed = factory.Session ?? throw new InvalidOperationException("Session was not constructed");
        _ = await Assert.That(factory.Lifetime.IsCancellationRequested).IsTrue();
        var log = await File.ReadAllTextAsync(constructed.Resources.LogPath);
        _ = await Assert.That(log).Contains("outcome=\"failed\"");
        using (var exclusive = new FileStream(constructed.Resources.LogPath, FileMode.Open, FileAccess.Write, FileShare.None))
        {
            _ = await Assert.That(exclusive.CanWrite).IsTrue();
        }

        Directory.Delete(constructed.Resources.MetadataPath);
        await using var retry = await Open(workingDirectory);
        _ = await Assert.That(retry.Id).IsEqualTo(constructed.Id);
    }

    [Test]
    public async Task Failed_lease_open_closes_log_and_releases_activation()
    {
        var paths = new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "lease-failure")).FullName;
        var id = UserSessionId.Parse("lease-failure");
        var resources = new UserSessionResources(paths, id, ProjectWorkspace.FromLaunchDirectory(workingDirectory));
        var claim = new WorkingDirectoryClaim(paths.State, "host");
        var admission = claim.CreateFresh(workingDirectory, id);
        var activation = admission.ActivationLease ?? throw new InvalidOperationException("Missing activation");
        using var diagnostics = Diagnostics();
        var log = diagnostics.OpenSession(resources);
        _ = Directory.CreateDirectory(resources.DatabasePath);

        _ = await Assert.That(() => SessionResourceLease.Open(resources, activation, log)).ThrowsException();
        var failedLog = await File.ReadAllTextAsync(resources.LogPath);
        _ = await Assert.That(failedLog).Contains("outcome=\"failed\"");
        log.Write(new DiagnosticEvent("test", "after-close", DiagnosticSeverity.Information));
        _ = await Assert.That(await File.ReadAllTextAsync(resources.LogPath)).IsEqualTo(failedLog);
        var retry = claim.Resume(workingDirectory, id);
        using var retryActivation = retry.ActivationLease;
        _ = await Assert.That(retryActivation).IsNotNull();
    }

    [Test]
    public async Task Resumed_and_legacy_sessions_retain_or_receive_a_durable_root_agent_name()
    {
        const string namedId = "user-session-named";
        const string legacyId = "user-session-legacy";
        var namedWork = Directory.CreateDirectory(Path.Combine(_root, "named-work")).FullName;
        var legacyWork = Directory.CreateDirectory(Path.Combine(_root, "legacy-work")).FullName;
        var namedIndex = new SessionIndex(new UserSessionResources(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse(namedId),
            ProjectWorkspace.FromLaunchDirectory(namedWork)));
        var legacyIndex = new SessionIndex(new UserSessionResources(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse(legacyId),
            ProjectWorkspace.FromLaunchDirectory(legacyWork)));
        namedIndex.Publish(new SessionMeta
        {
            Id = namedId,
            RootAgentName = "main",
            WorkingDirectory = namedWork,
            ProviderId = "unused",
            Model = "unused/model",
            CreatedAt = "2026-07-27T01:00:00Z",
        });
        legacyIndex.Publish(new SessionMeta
        {
            Id = legacyId,
            RootAgentName = string.Empty,
            WorkingDirectory = legacyWork,
            ProviderId = "unused",
            Model = "unused/model",
            CreatedAt = "2026-07-27T02:00:00Z",
        });
        StabilizeAdmission(namedWork, namedId);
        StabilizeAdmission(legacyWork, legacyId);
        var catalog = new SessionCatalog(new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")));
        {
            await using var named = await Open(namedWork);
            _ = await Assert.That(catalog.Find(UserSessionId.Parse(namedId))?.RootAgentName).IsEqualTo("main");
            _ = await Assert.That(catalog.Find(UserSessionId.Parse(namedId))?.CreatedAt)
                .IsEqualTo("2026-07-27T01:00:00Z");
        }

        await using var legacy = await Open(legacyWork);
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
        var index = new SessionIndex(new UserSessionResources(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse(id),
            ProjectWorkspace.FromLaunchDirectory(workingDirectory)));
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

        var catalog = new SessionCatalog(new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"))).List().Single(entry => entry.Id.Value == id);
        _ = await Assert.That(catalog.State).IsEqualTo(SessionCatalogState.Inactive);
        StabilizeAdmission(workingDirectory, id);

        await using var session = await Open(workingDirectory);
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
        var index = new SessionIndex(new UserSessionResources(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse(id),
            ProjectWorkspace.FromLaunchDirectory(workingDirectory)));
        index.Publish(new SessionMeta
        {
            Id = id,
            RootAgentName = "main",
            WorkingDirectory = workingDirectory,
            ProviderId = "unused",
            Model = "unused/model",
            CreatedAt = "2026-07-27T01:00:00Z",
        });
        var path = index.Resources.MetadataPath;
        var serialized = await File.ReadAllTextAsync(path);
        await File.WriteAllTextAsync(
            path,
            serialized.Replace("\"Selector\": \"\"", "\"Selector\": null", StringComparison.Ordinal));
        _ = await Assert.That(index.Find()?.Selector).IsNull();
        StabilizeAdmission(workingDirectory, id);

        await using var session = await Open(workingDirectory);
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
        var index = new SessionIndex(new UserSessionResources(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse(id),
            ProjectWorkspace.FromLaunchDirectory(linked)));
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

        await using var session = await Open(physical);
        var resumed = new SessionCatalog(new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"))).Find(UserSessionId.Parse(id));

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
        ILLMProvider provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var modes = Modes();
        var failing = new SessionStore(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            workingDirectory,
            "host",
            new ThrowingUserSessions(),
            TestModels.Route(model),
            modes,
            Diagnostics());

        _ = await Assert.That(async () =>
        {
            _ = await failing.Open(TestModels.Resolve(model));
        }).Throws<InvalidOperationException>();

        await using var session = await Open(workingDirectory);
        _ = await Assert.That(new SessionIndex(session.Resources).Find()?.RootAgentName).IsEqualTo("main");
        _ = await Assert.That(await File.ReadAllTextAsync(session.Resources.LogPath)).Contains("outcome=\"failed\"").And.Contains("event=\"start\"");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Explicit_resume_preserves_settings_and_open_publication(bool missingMetadata)
    {
        const string id = "user-session-explicit";
        var workingDirectory = Directory.CreateDirectory(Path.Combine(_root, "explicit-work")).FullName;
        var index = new SessionIndex(new UserSessionResources(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse(id),
            ProjectWorkspace.FromLaunchDirectory(workingDirectory)));
        if (!missingMetadata)
        {
            index.Publish(new SessionMeta
            {
                Id = id,
                RootAgentName = "main",
                WorkingDirectory = workingDirectory,
                ProviderId = "unused",
                Model = "unused/model",
                CreatedAt = "2026-01-01T00:00:00Z",
                Mode = ModeRegistry.Query,
            });
        }

        StabilizeAdmission(workingDirectory, id);
        var sessions = new DirectAgentSessions();
        ILLMProvider provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(model);
        sessions.Use(router);
        var store = new SessionStore(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            workingDirectory,
            "host",
            new UserSessionFactory(sessions, Modes(), TestModels.PromptTemplates, new TestProfileFixture().Registry, SkillCatalogFactory(), TimeSpan.FromSeconds(30), TimeProvider.System, TestModels.RuntimeStatusProviders),
            router,
            Modes(),
            Diagnostics());
        if (missingMetadata)
        {
            _ = await Assert.That(async () =>
            {
                _ = await store.Resume(UserSessionId.Parse(id), false);
            }).Throws<InvalidOperationException>();
            var retry = new WorkingDirectoryClaim(new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")).State, "host").Resume(workingDirectory, id);
            _ = await Assert.That(retry.Disposition).IsEqualTo(ClaimDisposition.Resumed);
            if (retry.ActivationLease is { } activation)
            {
                await activation.DisposeAsync();
            }

            return;
        }

        var opened = await store.Resume(UserSessionId.Parse(id), false);
        await using var session = opened.Session;
        _ = await Assert.That(opened.Loaded).IsTrue();
        _ = await Assert.That(session.Mode.Profile.Id).IsEqualTo(ModeRegistry.Query);
        _ = await Assert.That(session.Model).IsEqualTo("unused/model");
        _ = await Assert.That(session.Resources.SocketPath).IsEqualTo(Path.Combine(session.Resources.Root, "parrot.sock"));
        SessionStore.RecordOpened(session);
        var recorded = index.Find()?.LastOpenedAt;
        SessionStore.Publish(session);
        _ = await Assert.That(index.Find()?.LastOpenedAt).IsEqualTo(recorded);
        _ = await Assert.That(string.IsNullOrEmpty(recorded)).IsFalse();
        _ = await Assert.That(index.Find()?.CreatedAt).IsEqualTo("2026-01-01T00:00:00Z");
        _ = await Assert.That(store.DiscoverLatest().SessionId?.Value).IsEqualTo(id);
        var beforeContention = await File.ReadAllTextAsync(session.Resources.LogPath);
        _ = await Assert.That(async () =>
        {
            _ = await store.Resume(UserSessionId.Parse(id), false);
        }).Throws<SessionAdmissionException>();
        _ = await Assert.That(await File.ReadAllTextAsync(session.Resources.LogPath)).IsEqualTo(beforeContention);
        var logDirectory = new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")).LogDirectory;
        var globalLog = await File.ReadAllTextAsync(Directory.GetFiles(logDirectory, "parrot-*.log").Single());
        _ = await Assert.That(globalLog).Contains("event=\"acquire\"").And.Contains("outcome=\"failed\"");
    }

    private static SkillCatalogFactory SkillCatalogFactory()
    {
        var configuration = Configuration.Load(
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"), "config.yaml"),
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"), "predefined.yaml"));
        return new SkillCatalogFactory(configuration, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "packaged-skills"));
    }

    private Task<IUserSession> Open(string workingDirectory)
    {
        var sessions = new DirectAgentSessions();
        ILLMProvider provider = new UnusedProvider();
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(model);
        sessions.Use(router);
        var store = new SessionStore(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            workingDirectory,
            "host",
            new UserSessionFactory(sessions, Modes(), TestModels.PromptTemplates, new TestProfileFixture().Registry, SkillCatalogFactory(), TimeSpan.FromSeconds(30), TimeProvider.System, TestModels.RuntimeStatusProviders),
            router,
            Modes(),
            Diagnostics());
        return store.Open(router.Resolve(model.Selector));
    }

    private DiagnosticLogs Diagnostics()
    {
        var diagnostics = new DiagnosticLogs(
            new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            FileDiagnosticLog.CreateInstanceId(),
            TextWriter.Null,
            TimeProvider.System);
        _diagnostics.Add(diagnostics);
        return diagnostics;
    }

    private ModeRegistry Modes()
    {
        var paths = new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data"));
        var configuration = Configuration.Load(paths.ConfigFile, paths.PredefinedConfigFile);
        return new ModeRegistry(
            new ProfileRegistry(
                configuration.Profiles,
                configuration.SandboxRules,
                [],
                configuration.DisabledTools),
            configuration.DefaultProfile);
    }

    private void StabilizeAdmission(string workingDirectory, string sessionId)
    {
        var admission = new WorkingDirectoryClaim(new StatePaths(Path.Combine(_root, "state"), Path.Combine(_root, "config"), Path.Combine(_root, "data")).State, "host")
            .CreateFresh(workingDirectory, UserSessionId.Parse(sessionId));
        admission.ActivationLease?.Dispose();
    }

    private sealed class PublicationFailureUserSessions(IUserSessionFactory factory) : IUserSessionFactory
    {
        public IUserSession? Session { get; private set; }

        public CancellationToken Lifetime { get; private set; }

        public async Task<IUserSession> Create(
            ISessionResourceLease resources,
            string id,
            string rootAgentName,
            ResolvedModelSelection model,
            string mode,
            bool interactivePermissions)
        {
            Session = await factory.Create(resources, id, rootAgentName, model, mode, interactivePermissions);
            Lifetime = Session.Lifetime;
            _ = Directory.CreateDirectory(resources.Resources.MetadataPath);
            return Session;
        }
    }

    private sealed class ObservingUserSessions(IUserSessionFactory factory, string logPath, bool fail) : IUserSessionFactory
    {
        public string PendingLog { get; private set; } = string.Empty;

        public string SessionId { get; private set; } = string.Empty;

        public async Task<IUserSession> Create(
            ISessionResourceLease resources,
            string id,
            string rootAgentName,
            ResolvedModelSelection model,
            string mode,
            bool interactivePermissions)
        {
            SessionId = id;
            PendingLog = await File.ReadAllTextAsync(logPath);
            if (fail)
            {
                throw new InvalidOperationException("private-construction-failure");
            }

            return await factory.Create(resources, id, rootAgentName, model, mode, interactivePermissions);
        }
    }

    private sealed class ThrowingUserSessions : IUserSessionFactory
    {
        public Task<IUserSession> Create(
            ISessionResourceLease resources,
            string id,
            string rootAgentName,
            ResolvedModelSelection model,
            string mode,
            bool interactivePermissions) =>
            throw new InvalidOperationException("construction failed");
    }
}

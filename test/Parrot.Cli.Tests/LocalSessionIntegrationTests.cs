using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Skills;
using Parrot.State;
using Parrot.Store;
using Parrot.Web;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class LocalSessionIntegrationTests
{
    private const string Selection = "integration/model";

    [Test]
    [Skip("Probable lifecycle bug: shutdown materializes an unused root agent after ShellProcessOwners has stopped.")]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Startup_attaches_without_local_ownership_or_warns_and_creates_when_owner_is_unreachable(
        bool reachable, CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        using var workspace = new TestWorkspace();
        await using var owner = new TestRuntime(workspace, reachable);
        var original = await owner.Client.CreateSessionAsync(
            new CreateSessionRequest { Model = Selection, Mode = "plan", InteractivePermissions = true },
            cancellationToken: cancellationToken);
        await using var local = new TestRuntime(workspace, true);
        using var diagnostic = new StringWriter();
        var localOpens = 0;
        var configurations = 0;
        using (var startup = new LocalChatStartup(
            workspace.Paths,
            workspace.Root,
            "host",
            diagnostic,
            diagnostics.Log,
            token =>
            {
                token.ThrowIfCancellationRequested();
                localOpens++;
                return Task.FromResult(local.Client);
            },
            (client, token) =>
            {
                token.ThrowIfCancellationRequested();
                configurations++;
                return Task.FromResult(new CreateSessionRequest { Model = Selection, Mode = "query" });
            }))
        {
            var (_, opened) = await startup.Open(false, cancellationToken);
            _ = await Assert.That(opened.Id == original.Id).IsEqualTo(reachable);
            _ = await Assert.That(opened.Model).IsEqualTo(Selection);
            _ = await Assert.That(opened.Mode).IsEqualTo(reachable ? "plan" : "query");
            _ = await Assert.That(localOpens).IsEqualTo(reachable ? 0 : 1);
            _ = await Assert.That(configurations).IsEqualTo(reachable ? 0 : 1);
            _ = await Assert.That(Directory.GetFiles(workspace.Paths.State, "session.db", SearchOption.AllDirectories).Length)
                .IsEqualTo(reachable ? 1 : 2);
            _ = await Assert.That(diagnostic.ToString()).Contains(reachable
                ? $"connected to existing user session {original.Id}"
                : $"unable to connect to existing user session {original.Id}");
            if (!reachable)
            {
                _ = await Assert.That(diagnostic.ToString()).Contains("creating a new user session");
            }
            else
            {
                using var simultaneous = GrpcTransportClient.Connect(
                    TransportAddress.Parse($"unix:{new UserSessionResources(workspace.Paths, UserSessionId.Parse(original.Id), ProjectWorkspace.FromLaunchDirectory(workspace.Root)).SocketPath}"), null, diagnostics.Log);
                _ = await Assert.That((await simultaneous.Attach(
                    new AttachSessionRequest { UserSessionId = original.Id, WorkingDirectory = workspace.Root }, cancellationToken)).Id)
                    .IsEqualTo(original.Id);
            }
        }

        if (reachable)
        {
            using var second = GrpcTransportClient.Connect(
                TransportAddress.Parse($"unix:{new UserSessionResources(workspace.Paths, UserSessionId.Parse(original.Id), ProjectWorkspace.FromLaunchDirectory(workspace.Root)).SocketPath}"), null, diagnostics.Log);
            var attached = await second.Attach(
                new AttachSessionRequest { UserSessionId = original.Id, WorkingDirectory = workspace.Root }, cancellationToken);
            _ = await Assert.That(attached.Mode).IsEqualTo("plan");
            _ = await Assert.That(owner.Store.DiscoverLatest().Disposition).IsEqualTo(ClaimDisposition.Live);
        }
    }

    [Test]
    [Skip("Probable lifecycle bug: shutdown materializes an unused root agent after ShellProcessOwners has stopped.")]
    public async Task Reconnecting_older_session_updates_recency_and_owner_shutdown_allows_same_id_reload(
        CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        using var workspace = new TestWorkspace();
        string olderId;
        await using (var owner = new TestRuntime(workspace, true))
        {
            var older = await owner.Client.CreateSessionAsync(
                new CreateSessionRequest { Model = Selection, Mode = "plan" }, cancellationToken: cancellationToken);
            olderId = older.Id;
            var newer = await owner.Client.CreateSessionAsync(
                new CreateSessionRequest { Model = Selection, Mode = "query" }, cancellationToken: cancellationToken);
            var olderIndex = new SessionIndex(new UserSessionResources(workspace.Paths, UserSessionId.Parse(olderId), ProjectWorkspace.FromLaunchDirectory(workspace.Root)));
            var olderMeta = olderIndex.Find() ?? throw new InvalidOperationException("Missing older metadata.");
            olderIndex.Publish(olderMeta with { LastOpenedAt = "2000-01-01T00:00:00.0000000+00:00" });
            _ = await Assert.That(owner.Store.DiscoverLatest().SessionId?.Value).IsEqualTo(newer.Id);

            using var client = GrpcTransportClient.Connect(
                TransportAddress.Parse($"unix:{new UserSessionResources(workspace.Paths, UserSessionId.Parse(olderId), ProjectWorkspace.FromLaunchDirectory(workspace.Root)).SocketPath}"), null, diagnostics.Log);
            var attached = await client.Attach(
                new AttachSessionRequest { UserSessionId = olderId, WorkingDirectory = workspace.Root }, cancellationToken);
            _ = await Assert.That(attached.Mode).IsEqualTo("plan");
            _ = await Assert.That(owner.Store.DiscoverLatest().SessionId?.Value).IsEqualTo(olderId);
        }

        _ = await Assert.That(File.Exists(new UserSessionResources(workspace.Paths, UserSessionId.Parse(olderId), ProjectWorkspace.FromLaunchDirectory(workspace.Root)).SocketPath)).IsFalse();
        await using var replacement = new TestRuntime(workspace, true);
        _ = await Assert.That(replacement.Store.DiscoverLatest().Disposition).IsEqualTo(ClaimDisposition.Resumed);
        using var diagnostic = new StringWriter();
        using var startup = new LocalChatStartup(
            workspace.Paths,
            workspace.Root,
            "host",
            diagnostic,
            diagnostics.Log,
            token =>
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(replacement.Client);
            },
            (client, token) => throw new InvalidOperationException("Reload must not configure fresh settings."));
        var (_, loaded) = await startup.Open(true, cancellationToken);
        _ = await Assert.That(loaded.Id).IsEqualTo(olderId);
        _ = await Assert.That(loaded.Loaded).IsTrue();
        _ = await Assert.That(loaded.Model).IsEqualTo(Selection);
        _ = await Assert.That(loaded.Mode).IsEqualTo("plan");
        _ = await Assert.That(diagnostic.ToString()).Contains($"loaded existing user session {olderId}");
        _ = await Assert.That(Directory.GetFiles(workspace.Paths.State, "session.db", SearchOption.AllDirectories).Length).IsEqualTo(2);
        using var reattached = GrpcTransportClient.Connect(
            TransportAddress.Parse($"unix:{new UserSessionResources(workspace.Paths, UserSessionId.Parse(olderId), ProjectWorkspace.FromLaunchDirectory(workspace.Root)).SocketPath}"), null, diagnostics.Log);
        _ = await Assert.That((await reattached.Attach(
            new AttachSessionRequest { UserSessionId = olderId, WorkingDirectory = workspace.Root }, cancellationToken)).Id)
            .IsEqualTo(olderId);
    }

    private sealed class TestWorkspace : IDisposable
    {
        public TestWorkspace()
        {
            Root = Path.Combine("/tmp", "pi", Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Root);
            Paths = new StatePaths(Path.Combine(Root, "s"), Path.Combine(Root, "c"), Path.Combine(Root, "d"));
        }

        public string Root { get; }

        public StatePaths Paths { get; }

        public void Dispose() => Directory.Delete(Root, recursive: true);
    }

    private sealed class TestRuntime : IAsyncDisposable
    {
        private readonly WebFetcher _webFetcher = WebFetcher.Create(new PublicWebAddressPolicy());
        private readonly ParrotService _service;
        private readonly DiagnosticLogs _diagnostics;

        public TestRuntime(TestWorkspace workspace, bool reachable)
        {
            var configuration = Configuration.Load(workspace.Paths.ConfigFile, workspace.Paths.PredefinedConfigFile);
            ILLMProvider provider = new TestProvider();
            var registry = new ProviderRegistry(
                [provider],
                new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
                {
                    [provider.Id] = [new("model", provider.Id) { ContextWindow = 100_000 }],
                });
            var aliases = new ModelAliasCatalog(registry, []);
            var routing = new ModelRouting(aliases, Selection);
            var router = new ModelRouter(registry, routing);
            var models = new ModelConfigurationCoordinator(configuration, routing, router);
            var profiles = new ProfileRegistry(configuration.Profiles, configuration.SandboxRules, [], configuration.DisabledTools);
            var modes = new ModeRegistry(profiles, configuration.DefaultProfile);
            var source = new AgentSessionFactorySource(
                ProcessRunner.Locate(ExecutableLocator.Capture()),
                new Compactor(90, 30, 60_000, 1024, configuration.PromptTemplates),
                _webFetcher,
                configuration.ToolDefinitions,
                configuration.AgentTasks,
                configuration.ReadOnlyExecCommandPrefixes,
                router,
                new CompositeSystemPromptProvider("test:integration", []),
                configuration.PromptTemplates);
            var factory = new UserSessionFactory(
                source,
                modes,
                configuration.PromptTemplates,
                profiles,
                new SkillCatalogFactory(configuration, workspace.Root, Path.Combine(workspace.Root, "skills")),
                TimeSpan.FromSeconds(30),
                TimeProvider.System);
            _diagnostics = new DiagnosticLogs(workspace.Paths, FileDiagnosticLog.CreateInstanceId(), TextWriter.Null, TimeProvider.System);
            Store = new SessionStore(workspace.Paths, workspace.Root, "host", factory, router, modes, _diagnostics);
            _service = new ParrotService(
                router,
                registry,
                new ModelAliasConfigurator(models),
                models,
                Store,
                new SessionCatalog(workspace.Paths),
                modes,
                reachable ? new LocalUserSessionHost() : new UnexposedUserSessionHost(),
                _diagnostics.Global);
            Client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(_service));
        }

        public SessionStore Store { get; }

        public GeneratedParrot.ParrotClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await _service.DisposeAsync();
            }
            finally
            {
                _diagnostics.Dispose();
            }
        }
    }

    private sealed class TestProvider : ILLMProvider
    {
        public string Id => "integration";

        public IReadOnlyList<LLMModel> SeedModels() => [new("model", Id) { ContextWindow = 100_000 }];

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([new("model", Id)]);

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Session startup must not call the provider.");
    }
}

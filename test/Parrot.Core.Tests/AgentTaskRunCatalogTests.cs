using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskRunCatalogTests : IAsyncDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly IEventBroker _broker = new EventBroker();
    private readonly List<IAgentRegistry> _registries = [];
    private readonly List<IProcessOwner> _processOwners = [];
    private readonly IEventRepository _repository;

    public AgentTaskRunCatalogTests() => _repository = new EventRepository(_database);

    public async ValueTask DisposeAsync()
    {
        foreach (var processOwners in _processOwners)
        {
            await processOwners.DisposeAsync();
        }

        foreach (var registry in _registries)
        {
            await registry.DisposeAsync().ConfigureAwait(false);
        }

        _broker.Dispose();
        _database.Dispose();
    }

    [Test]
    [Arguments(AgentTaskExecutionStatus.Succeeded)]
    [Arguments(AgentTaskExecutionStatus.Failed)]
    [Arguments(AgentTaskExecutionStatus.Canceled)]
    public async Task Background_run_logs_its_eventual_terminal_status(
        AgentTaskExecutionStatus status,
        CancellationToken cancellationToken)
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-run-diagnostics", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var resources = new UserSessionResources(
                new State.StatePaths(Path.Combine(directory, "state"), Path.Combine(directory, "config"), Path.Combine(directory, "data")),
                UserSessionId.Parse("session-diagnostics"),
                ProjectWorkspace.FromLaunchDirectory(directory));
            using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
            using var provider = new AgentTaskBlockingProvider();
            ILLMProvider selectedProvider = status == AgentTaskExecutionStatus.Canceled
                ? provider
                : new AgentTaskQueueProvider([status == AgentTaskExecutionStatus.Succeeded
                    ? "{\"result\":\"secret-result\",\"verdict\":\"accept\",\"evidence\":\"secret-evidence\"}"
                    : "secret-invalid-response"]);
            var runtime = Runtime(selectedProvider, cancellationToken);
            await using var registry = runtime.Registry;
            await using var catalog = new AgentTaskRunCatalog(runtime.Parent.SessionId, diagnostics, cancellationToken);
            var completion = new Completion();
            catalog.Start(
                new AgentTaskRunRequest(
                    "run-id",
                    "secret-display-name",
                    AgentTaskParser.ParseArtifact(
                        """
                        {"schema_version":1,"tasks":[{"name":"secret-name","description":"secret-description","payload":"secret-payload","acceptance_criteria":"secret-criteria"}]}
                        """),
                    runtime.Router,
                    runtime.ParentScope,
                    runtime.Selection,
                    new AgentTaskProgress(_broker, _repository, runtime.Parent.SessionId, "run-id", diagnostics),
                    new AgentTaskConfig(1, 3, true, TestModels.PromptTemplates),
                    new HistoryForkBoundary.AfterCompletedHistory(),
                    completion),
                cancellationToken);
            if (status == AgentTaskExecutionStatus.Canceled)
            {
                await provider.WaitUntilArrived(cancellationToken);
            }
            else
            {
                _ = await completion.Delivered.WaitAsync(cancellationToken);
            }

            await catalog.Settle();
            var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
            _ = await Assert.That(log).Contains("category=\"task_run\" event=\"start\"")
                .And.Contains("category=\"task_run\" event=\"completed\"")
                .And.Contains($"outcome=\"{status.ToString().ToLowerInvariant()}\"")
                .And.Contains("correlation=\"run-id\"")
                .And.DoesNotContain("secret-");
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public async Task Runs_are_owner_scoped_isolated_from_the_call_token_and_settle_with_the_catalog(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var catalog = runtime.ParentScope.GetService<IAgentTaskRunCatalog>();
        await using var otherOwner = new AgentTaskRunCatalog("other-owner", TestDiagnosticLog.Instance, cancellationToken);
        using var call = new CancellationTokenSource();
        var first = new AgentTaskProgress(_broker, _repository, runtime.Parent.SessionId, "first", TestDiagnosticLog.Instance);
        var second = new AgentTaskProgress(_broker, _repository, runtime.Parent.SessionId, "second", TestDiagnosticLog.Instance);
        var firstCompletion = new Completion();
        var secondCompletion = new Completion();

        catalog.Start(
            new AgentTaskRunRequest(
                "first",
                "first",
                AgentTaskParser.ParseArtifact("""
                    {"schema_version":1,"tasks":[{"name":"first","description":"Run first","payload":"work","acceptance_criteria":"Done"}]}
                    """),
                runtime.Router,
                runtime.ParentScope,
                runtime.Selection,
                first,
                new AgentTaskConfig(1, 3, true, TestModels.PromptTemplates),
                new HistoryForkBoundary.AfterCompletedHistory(),
                firstCompletion),
            call.Token);
        catalog.Start(
            new AgentTaskRunRequest(
                "second",
                "second",
                AgentTaskParser.ParseArtifact("""
                    {"schema_version":1,"tasks":[{"name":"second","description":"Run second","payload":"work","acceptance_criteria":"Done"}]}
                    """),
                runtime.Router,
                runtime.ParentScope,
                runtime.Selection,
                second,
                new AgentTaskConfig(1, 3, true, TestModels.PromptTemplates),
                new HistoryForkBoundary.AfterCompletedHistory(),
                secondCompletion),
            call.Token);

        var admittedSnapshots = _repository.Replay()
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.AgentTaskProgressSnapshot)
            .Select(published => published.AgentTaskProgressSnapshot)
            .GroupBy(snapshot => snapshot.OriginToolCallId, StringComparer.Ordinal)
            .Select(group => group.First())
            .ToArray();
        _ = await Assert.That(string.Join(',', admittedSnapshots.Select(snapshot => snapshot.OriginToolCallId).Order(StringComparer.Ordinal)))
            .IsEqualTo("first,second");
        _ = await Assert.That(admittedSnapshots.All(snapshot => snapshot.Revision == 1UL)).IsTrue();
        _ = await Assert.That(admittedSnapshots.All(snapshot => snapshot.RootNodes.Count == 1)).IsTrue();
        _ = await Assert.That(admittedSnapshots.All(snapshot => snapshot.RootNodes.Single().Status == AgentTaskProgressStatus.Pending)).IsTrue();
        await provider.Arrived(cancellationToken);
        await provider.Arrived(cancellationToken);
        await call.CancelAsync();

        var snapshots = catalog.Snapshot();
        _ = await Assert.That(snapshots.All(snapshot => snapshot.Progress.Revision >= 1UL)).IsTrue();
        _ = await Assert.That(snapshots.All(snapshot => snapshot.Progress.RootNodes.Count == 1)).IsTrue();
        _ = await Assert.That(snapshots.All(snapshot => snapshot.Progress.RootNodes.Single().Status == AgentTaskProgressStatus.Running)).IsTrue();
        _ = await Assert.That(string.Join(',', snapshots.Select(snapshot => snapshot.RunId)))
            .IsEqualTo("first,second");
        _ = await Assert.That(string.Join(',', snapshots.Select(snapshot => snapshot.Progress.OriginToolCallId)))
            .IsEqualTo("first,second");
        _ = await Assert.That(snapshots.Select(snapshot => snapshot.Progress.Revision).Distinct().Count())
            .IsEqualTo(1);
        _ = await Assert.That(otherOwner.Snapshot()).IsEmpty();
        _ = await Assert.That(catalog.Active()).Count().IsEqualTo(2);
        var reminder = new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(runtime.ParentScope.ChildRegistry, runtime.Parent.Identity), new ProcessActiveWorkBlocker(runtime.Processes), new AgentTaskActiveWorkBlocker(catalog, TestModels.PromptTemplates), new QueueActiveWorkBlocker(runtime.ParentScope.GetService<IAgentQueues>(), TestModels.PromptTemplates)], TestModels.PromptTemplates).Build();
        _ = await Assert.That(reminder).Contains("Running AgentTask graphs:");
        _ = await Assert.That(reminder).Contains($"{runtime.Parent.SessionId}/first (name: first)");
        _ = await Assert.That(reminder).Contains($"{runtime.Parent.SessionId}/second (name: second)");
        var statusProvider = new AgentTaskStatusProvider(catalog, TestModels.PromptTemplates);
        var status = await statusProvider.Observe(
            new StatusQuery(runtime.Parent.SessionId, string.Empty, string.Empty, "profile", "model"),
            cancellationToken);
        _ = await Assert.That(status.Available).IsTrue();
        _ = await Assert.That(status.Text.IndexOf("first", StringComparison.Ordinal))
            .IsLessThan(status.Text.IndexOf("second", StringComparison.Ordinal));
        _ = await Assert.That(status.Text).Contains("task: first (");
        _ = await Assert.That(status.Text).Contains("description: Run first)");
        _ = await Assert.That(status.Text).Contains("task: second (");
        _ = await Assert.That(status.Text).Contains("description: Run second)");
        var customTemplates = new PromptTemplateCatalog(new Dictionary<string, PromptTemplate>(StringComparer.Ordinal)
        {
            ["status.runtime"] = new(
                new HashSet<string>(["section", "agents", "runs"], StringComparer.Ordinal),
                new HashSet<string>(["section", "agents", "runs"], StringComparer.Ordinal),
                new ScribanPromptTemplateEngine("prompt_templates.status.runtime", "{{ section }}:{{ for run in runs }}{{ run.run_id }}={{ for node in run.nodes }}{{ node.name }};{{ end }}{{ end }}")),
        });
        var customizedStatus = await new AgentTaskStatusProvider(catalog, customTemplates).Observe(
            new StatusQuery(runtime.Parent.SessionId, string.Empty, string.Empty, "profile", "model"),
            cancellationToken);
        _ = await Assert.That(statusProvider.Key).IsEqualTo("runtime:agent-tasks");
        _ = await Assert.That(customizedStatus.Text).IsEqualTo("agent-tasks:first=first;second=second;");

        var settlement = catalog.Settle();
        _ = await Assert.That(() => catalog.Start(
                new AgentTaskRunRequest(
                    "third",
                    "third",
                    AgentTaskParser.ParseArtifact("""
                        {"schema_version":1,"tasks":[{"name":"third","description":"Run third","payload":"work","acceptance_criteria":"Done"}]}
                        """),
                    runtime.Router,
                    runtime.ParentScope,
                    runtime.Selection,
                    new AgentTaskProgress(_broker, _repository, runtime.Parent.SessionId, "third", TestDiagnosticLog.Instance),
                    new AgentTaskConfig(1, 3, true, TestModels.PromptTemplates),
                    new HistoryForkBoundary.AfterCompletedHistory(),
                    new Completion()),
                cancellationToken))
            .Throws<InvalidOperationException>();
        await settlement;

        _ = await Assert.That(catalog.Snapshot()).IsEmpty();
        var retained = runtime.ParentScope.ChildRegistry.SnapshotDescendants();
        _ = await Assert.That(retained).Count().IsEqualTo(2);
        _ = await Assert.That(retained.Any(session => session.IsActive())).IsFalse();
        _ = await Assert.That(string.Join(',', retained.Select(session => session.Name).Order(StringComparer.Ordinal)))
            .IsEqualTo("first,second");
        _ = await Assert.That((await firstCompletion.Delivered).Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
        _ = await Assert.That((await secondCompletion.Delivered).Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
    }

    [Test]
    public async Task Transient_completion_failure_retries_with_one_stable_message_id(
        CancellationToken cancellationToken)
    {
        using var provider = new AgentTaskBlockingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var catalog = runtime.ParentScope.GetService<IAgentTaskRunCatalog>();
        var completion = new FailOnceCompletion();
        var progress = new AgentTaskProgress(_broker, _repository, runtime.Parent.SessionId, "retry-delivery", TestDiagnosticLog.Instance);
        var request = new AgentTaskRunRequest(
            "retry-delivery",
            "leaf",
            AgentTaskParser.ParseArtifact(
                """
                {"schema_version":1,"tasks":[{"name":"leaf","description":"Leaf","payload":"work","acceptance_criteria":"Done"}]}
                """),
            runtime.Router,
            runtime.ParentScope,
            runtime.Selection,
            progress,
            new AgentTaskConfig(1, 3, true, TestModels.PromptTemplates),
            new HistoryForkBoundary.AfterCompletedHistory(),
            completion);

        catalog.Start(request, cancellationToken);
        await provider.WaitUntilArrived(cancellationToken);
        await catalog.Settle();

        var delivered = await completion.Delivered;
        _ = await Assert.That(completion.Attempts).IsEqualTo(2);
        _ = await Assert.That(delivered.RunId).IsEqualTo("retry-delivery");
        _ = await Assert.That(delivered.CompletionMessageId).IsNotEmpty();
        _ = await Assert.That(delivered.Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
        _ = await Assert.That(catalog.Active()).IsEmpty();
    }

    [Test]
    public async Task Retired_run_id_can_be_reused_without_settling_the_owner(CancellationToken cancellationToken)
    {
        var provider = new AgentTaskQueueProvider([
            "{\"result\":\"done\",\"verdict\":\"accept\",\"evidence\":\"done\"}",
            "{\"result\":\"done\",\"verdict\":\"accept\",\"evidence\":\"done\"}"]);
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        var catalog = runtime.ParentScope.GetService<IAgentTaskRunCatalog>();
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var completion = new Completion();
            catalog.Start(
                new AgentTaskRunRequest(
                    "reusable",
                    "reusable",
                    AgentTaskParser.ParseArtifact("""
                        {"schema_version":1,"tasks":[{"name":"worker","description":"Run work","payload":"work","acceptance_criteria":"Done"}]}
                        """),
                    runtime.Router,
                    runtime.ParentScope,
                    runtime.Selection,
                    new AgentTaskProgress(_broker, _repository, runtime.Parent.SessionId, $"call-{attempt}", TestDiagnosticLog.Instance),
                    new AgentTaskConfig(1, 3, true, TestModels.PromptTemplates),
                    new HistoryForkBoundary.AfterCompletedHistory(),
                    completion),
                cancellationToken);
            _ = await Assert.That((await completion.Delivered.WaitAsync(cancellationToken)).Status).IsEqualTo(AgentTaskExecutionStatus.Succeeded);
            while (catalog.Active().Count != 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Yield();
            }
        }

        _ = await Assert.That(catalog.Snapshot()).IsEmpty();
    }

    private RuntimeContext Runtime(ILLMProvider provider, CancellationToken cancellationToken)
    {
        var processRoot = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-agent-task-catalog-tests", Guid.NewGuid().ToString("N"))).FullName;
        var processResources = new UserSessionResources(
            new State.StatePaths(processRoot, processRoot, processRoot),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(processRoot));
        var identity = AgentIdentity.Main("agent-task-catalog-parent", "parent", TestModels.PromptTemplates);
        var processOwner = new ShellProcessOwner(
            identity,
            processResources,
            new AgentPathEnvironment(processResources, processResources.AgentScratch(identity.SessionId)),
            new ProcessRunner(string.Empty),
            TestDiagnosticLog.Instance,
            cancellationToken);
        _processOwners.Add(processOwner);
        var model = new LLMModel("model", provider.Id);
        var providers = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { [provider.Id] = [model] });
        var router = new ModelRouter(providers, new ModelRouting(new ModelAliasCatalog(providers, []), $"{provider.Id}/model"));
        var sessions = new AgentTaskTestSessionFactory(router);
        var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            cancellationToken);
        _registries.Add(registry);
        using var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        using var parentScope = TestAgentSessionScope.Build(identity, AgentSessionParentLink.Root(), registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) => new AgentSession(
            identity,
            sessionParentScope,
            new ModelSelector($"{provider.Id}/model"),
            router,
            _broker,
            _repository,
            [],
            TestModels.MaterializePrompt(identity, ".", "."),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new AgentOutputFile(Path.GetTempPath()),
            TestModels.CompactionGroupBlobs(),
            new Parrot.Context.Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates),
            new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null),
            new Parrot.Context.ContextCadence(),
            TestModels.PromptTemplates,
            childQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            new TestCompletionCallbacksFixture(childQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, _repository, _broker).Callbacks,
            new SecurityProfileTestFixture(SecurityProfile.Compose(false, [], [], [])).Security,
            dependencies.Status,
            new AgentSessionActivity(TimeProvider.System),
            TestDiagnosticLog.Instance,
            cancellationToken));
        registry.RegisterRootScope(parentScope);
        var selected = parentScope.Session.CurrentSelection();
        return new RuntimeContext(
            router,
            registry,
            parentScope,
            parentScope.Session,
            processOwner,
            new AgentTurnSelection(
                selected.RequestedModel,
                router.Resolve(selected.RequestedModel.Value),
                selected.Mode,
                selected.SecurityProfile));
    }

    private sealed record RuntimeContext(
        IModelRouter Router,
        IAgentRegistry Registry,
        IAgentSessionScope ParentScope,
        IAgentSession Parent,
        IProcessOwner Processes,
        AgentTurnSelection Selection);

    private sealed class Completion : IAgentTaskRunCompletion
    {
        private readonly TaskCompletionSource<AgentTaskRunTerminal> _delivered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task<AgentTaskRunTerminal> Delivered => _delivered.Task;

        public Task Deliver(AgentTaskRunTerminal terminal, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            _ = _delivered.TrySetResult(terminal);
            return Task.CompletedTask;
        }

        public Task DeliverDuringShutdown(AgentTaskRunTerminal terminal, CancellationToken cancellationToken) =>
            Deliver(terminal, cancellationToken);
    }

    private sealed class FailOnceCompletion : IAgentTaskRunCompletion
    {
        private readonly TaskCompletionSource<AgentTaskRunTerminal> _delivered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private int _attempts;

        internal int Attempts => Volatile.Read(ref _attempts);

        internal Task<AgentTaskRunTerminal> Delivered => _delivered.Task;

        public Task Deliver(AgentTaskRunTerminal terminal, CancellationToken cancellationToken)
        {
            _ = cancellationToken;
            if (Interlocked.Increment(ref _attempts) == 1)
            {
                throw new IOException("transient delivery failure");
            }

            _ = _delivered.TrySetResult(terminal);
            return Task.CompletedTask;
        }

        public Task DeliverDuringShutdown(AgentTaskRunTerminal terminal, CancellationToken cancellationToken) =>
            Deliver(terminal, cancellationToken);
    }
}

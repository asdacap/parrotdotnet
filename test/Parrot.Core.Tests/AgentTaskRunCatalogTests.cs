using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Statuses;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class AgentTaskRunCatalogTests : IDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();
    private readonly List<ShellProcessOwners> _processOwners = [];
    private readonly EventRepository _repository;

    public AgentTaskRunCatalogTests() => _repository = new EventRepository(_database);

    public void Dispose()
    {
        foreach (var processOwners in _processOwners)
        {
            processOwners.Dispose();
        }

        _broker.Dispose();
        _database.Dispose();
    }

    [Test]
    public async Task Runs_are_owner_scoped_isolated_from_the_call_token_and_settle_with_the_catalog(
        CancellationToken cancellationToken)
    {
        using var provider = new AgentTaskBlockingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        await using var catalog = new AgentTaskRunCatalog(cancellationToken);
        var owner = catalog.Prepare(runtime.Parent.SessionId);
        var otherOwner = catalog.Prepare("other-owner");
        using var call = new CancellationTokenSource();
        var first = Progress("first");
        var second = Progress("second");
        var firstCompletion = new Completion();
        var secondCompletion = new Completion();

        owner.Start(Request("first", first, firstCompletion), cancellationToken);
        owner.Start(Request("second", second, secondCompletion), cancellationToken);

        var admittedSnapshots = owner.Snapshot();
        _ = await Assert.That(admittedSnapshots.All(snapshot => snapshot.Progress.Revision == 1UL)).IsTrue();
        _ = await Assert.That(admittedSnapshots.All(snapshot => snapshot.Progress.RootNodes.Count == 1)).IsTrue();
        _ = await Assert.That(admittedSnapshots.All(snapshot => snapshot.Progress.RootNodes.Single().Status == AgentTaskProgressStatus.Pending)).IsTrue();
        await provider.WaitUntilArrived(cancellationToken);
        await call.CancelAsync();

        var snapshots = owner.Snapshot();
        _ = await Assert.That(snapshots.All(snapshot => snapshot.Progress.Revision >= 1UL)).IsTrue();
        _ = await Assert.That(snapshots.All(snapshot => snapshot.Progress.RootNodes.Count == 1)).IsTrue();
        _ = await Assert.That(string.Join(',', snapshots.Select(snapshot => snapshot.RunId)))
            .IsEqualTo("first,second");
        _ = await Assert.That(string.Join(',', snapshots.Select(snapshot => snapshot.Progress.OriginToolCallId)))
            .IsEqualTo("first,second");
        _ = await Assert.That(snapshots.Select(snapshot => snapshot.Progress.Revision).Distinct().Count())
            .IsEqualTo(1);
        _ = await Assert.That(otherOwner.Snapshot()).IsEmpty();
        _ = await Assert.That(owner.Active()).Count().IsEqualTo(2);
        var reminder = new ActiveWorkCompletionReminder(
            runtime.ParentScope.ChildRegistry,
            runtime.Processes,
            TestModels.PromptTemplates,
            owner).Build();
        _ = await Assert.That(reminder).Contains("Running AgentTask graphs:");
        _ = await Assert.That(reminder).Contains($"{runtime.Parent.SessionId}/first (name: first)");
        _ = await Assert.That(reminder).Contains($"{runtime.Parent.SessionId}/second (name: second)");
        var statusProvider = new AgentTaskStatusProvider(catalog, TestModels.PromptTemplates);
        var status = await statusProvider.Observe(
            new StatusQuery(runtime.Parent.SessionId, string.Empty, string.Empty, "profile", "model"),
            cancellationToken);
        var otherStatus = await statusProvider.Observe(
            new StatusQuery("other-owner", string.Empty, string.Empty, "profile", "model"),
            cancellationToken);
        _ = await Assert.That(status.Available).IsTrue();
        _ = await Assert.That(status.Text.IndexOf("first", StringComparison.Ordinal))
            .IsLessThan(status.Text.IndexOf("second", StringComparison.Ordinal));
        _ = await Assert.That(status.Text).Contains("task: first (");
        _ = await Assert.That(status.Text).Contains("description: Run first)");
        _ = await Assert.That(status.Text).Contains("task: second (");
        _ = await Assert.That(status.Text).Contains("description: Run second)");
        _ = await Assert.That(otherStatus.Available).IsFalse();

        var settlement = catalog.Settle();
        _ = await Assert.That(() => owner.Start(
                Request("third", Progress("third"), new Completion()),
                cancellationToken))
            .Throws<InvalidOperationException>();
        await settlement;

        _ = await Assert.That(owner.Snapshot()).IsEmpty();
        _ = await Assert.That(runtime.ParentScope.ChildRegistry.SnapshotDescendants()).IsEmpty();
        _ = await Assert.That((await firstCompletion.Delivered).Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
        _ = await Assert.That((await secondCompletion.Delivered).Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);

        AgentTaskProgress Progress(string callId) =>
            new(_broker, _repository, runtime.Parent.SessionId, callId);

        AgentTaskRunRequest Request(
            string runId,
            AgentTaskProgress progress,
            IAgentTaskRunCompletion completion) => new(
                runId,
                runId,
                AgentTaskParser.ParseArtifact($$"""
                    {"schema_version":1,"tasks":[{"name":"{{runId}}","description":"Run {{runId}}","payload":"work","acceptance_criteria":"Done"}]}
                    """),
                runtime.Router,
                runtime.ParentScope,
                runtime.Selection,
                progress,
                new AgentTaskConfig(1, true, TestModels.PromptTemplates),
                new HistoryForkBoundary.AfterCompletedHistory(),
                completion);
    }

    [Test]
    public async Task Transient_completion_failure_retries_with_one_stable_message_id(
        CancellationToken cancellationToken)
    {
        using var provider = new AgentTaskBlockingProvider();
        var runtime = Runtime(provider, cancellationToken);
        await using var registry = runtime.Registry;
        await using var catalog = new AgentTaskRunCatalog(cancellationToken);
        var owner = catalog.Prepare(runtime.Parent.SessionId);
        var completion = new FailOnceCompletion();
        var progress = new AgentTaskProgress(_broker, _repository, runtime.Parent.SessionId, "retry-delivery");
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
            new AgentTaskConfig(1, true, TestModels.PromptTemplates),
            new HistoryForkBoundary.AfterCompletedHistory(),
            completion);

        owner.Start(request, cancellationToken);
        await provider.WaitUntilArrived(cancellationToken);
        await catalog.Settle();

        var delivered = await completion.Delivered;
        _ = await Assert.That(completion.Attempts).IsEqualTo(2);
        _ = await Assert.That(delivered.RunId).IsEqualTo("retry-delivery");
        _ = await Assert.That(delivered.CompletionMessageId).IsNotEmpty();
        _ = await Assert.That(delivered.Status).IsEqualTo(AgentTaskExecutionStatus.Canceled);
        _ = await Assert.That(owner.Active()).IsEmpty();
    }

    private RuntimeContext Runtime(AgentTaskBlockingProvider provider, CancellationToken cancellationToken)
    {
        var processRoot = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "parrot-agent-task-catalog-tests", Guid.NewGuid().ToString("N"))).FullName;
        var processResources = new UserSessionResources(
            new State.StatePaths(processRoot, processRoot, processRoot),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(processRoot));
        var processOwners = new ShellProcessOwners(
            processResources,
            new ProcessRunner(string.Empty),
            cancellationToken);
        _processOwners.Add(processOwners);
        var processOwner = processOwners.Prepare("agent-task-catalog-parent");
        processOwners.Register(processOwner);
        var model = new LLMModel("model", provider.Id);
        var providers = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal) { [provider.Id] = [model] });
        var router = new ModelRouter(providers, new ModelAliasCatalog(providers, []), $"{provider.Id}/model");
        var sessions = new AgentTaskTestSessionFactory(router);
        var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var identity = AgentIdentity.Main("agent-task-catalog-parent", "parent", TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        using var parentScope = TestAgentSessionScope.Build(identity, AgentSessionParentLink.Root(), registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) => new AgentSession(
            identity,
            sessionParentScope,
            new ModelSelector($"{provider.Id}/model"),
            router,
            _broker,
            _repository,
            [],
            TestModels.EmptyToolDefinitions,
            TestModels.MaterializePrompt(identity, ".", "."),
            new ToolOutputBlobStore(Path.GetTempPath()),
            TestModels.CompactionGroupBlobs(),
            new Parrot.Context.Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates),
            new Parrot.Context.ContextCadence(),
            TestModels.PromptTemplates,
            childQuestions,
            dependencies.ExitReminder,
            dependencies.Profile,
            TestModels.CompletionCallbacks(childQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, _repository, _broker),
            SecurityProfileTestFactory.Create(SecurityProfile.Compose(false, [], [], [])),
            dependencies.Status,
            dependencies.Queues,
            new AgentSessionActivity(TimeProvider.System),
            cancellationToken));
        registry.RegisterRootScope(parentScope);
        var selected = parentScope.Session.Selection();
        return new RuntimeContext(
            router,
            registry,
            parentScope,
            parentScope.Session,
            processOwner,
            new AgentTurnSelection(
                selected.RequestedModel,
                router.Resolve(selected.RequestedModel.Value),
                selected.Profile,
                selected.SecurityProfile));
    }

    private sealed record RuntimeContext(
        ModelRouter Router,
        AgentRegistry Registry,
        IAgentSessionScope ParentScope,
        IAgentSession Parent,
        ShellProcessOwner Processes,
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

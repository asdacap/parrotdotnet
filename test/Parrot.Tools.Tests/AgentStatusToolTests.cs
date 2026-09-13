using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class AgentStatusToolTests : IAsyncDisposable
{
    private readonly IEventBroker _broker = new EventBroker();
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly IEventRepository _repository;
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), "parrot-agent-status-tests", Guid.NewGuid().ToString("N"))).FullName;

    public AgentStatusToolTests() => _repository = new EventRepository(_database);

    public async ValueTask DisposeAsync()
    {
        _broker.Dispose();
        _database.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }

        await ValueTask.CompletedTask;
    }

    [Test]
    public async Task Reports_direct_child_activity_and_rejects_non_direct_canonical_id(
        CancellationToken cancellationToken)
    {
        var time = new ControlledTimeProvider();
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var router = TestModels.Route(new ProviderModel(provider, new LLMModel("model", provider.Id)));
        var resources = new UserSessionResources(
            new StatePaths(_root, _root, _root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(_root));
        var factory = new StatusAgentSessions(router, resources, time);
        await using IAgentRegistry registry = new AgentRegistry(
            factory,
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1024),
            TestDiagnosticLog.Instance,
            cancellationToken);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        await using var parentScope = factory.Build(
            AgentIdentity.Main("parent", "main", TestModels.PromptTemplates),
            AgentSessionParentLink.Root(),
            registry,
            status,
            _repository,
            _broker,
            cancellationToken);
        registry.RegisterRootScope(parentScope);
        var parent = parentScope.Session;
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            parent.CurrentSelection().RequestedModel,
            "child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var grandchild = TestModels.ScopeOf(child).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            child,
            new TurnFixture(child, router).Selection,
            "worker",
            child.CurrentSelection().RequestedModel,
            "grandchild",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        _ = await child.SendTextMessage("work", cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await grandchild.SendTextMessage("work", cancellationToken);
        await provider.Arrived(cancellationToken);
        child.Activity.ObserveProviderEvent(LLMEvent.TextDelta("token"));
        child.Activity.RecordAssistantMessage("line one\nline two");
        time.Advance(TimeSpan.FromSeconds(2));
        var toolExecution = child.Activity.BeginTool("wait");
        ITool tool = new AgentStatusTool(
            new AgentResolver(parent.Identity, TestModels.ScopeOf(parent).ParentScope, TestModels.ScopeOf(parent), registry),
            TestModels.ScopeOf(parent).ParentScope);

        var report = (await tool.Execute(
            new ToolInvocation("call", "{\"session_id\":\"child\"}"),
            new TurnFixture(parent, router).Selection,
            cancellationToken)).Text;
        var rejected = (await tool.Execute(
            new ToolInvocation("call", $"{{\"session_id\":\"{grandchild.SessionId}\"}}"),
            new TurnFixture(parent, router).Selection,
            cancellationToken)).Text;

        _ = await Assert.That(report).Contains($"Session: {child.SessionId}");
        _ = await Assert.That(report).Contains("Request session duration: 2.0s");
        _ = await Assert.That(report).Contains("Current provider request duration: 2.0s");
        _ = await Assert.That(report).Contains("Current tool: wait");
        _ = await Assert.That(report).Contains($"- grandchild ({grandchild.SessionId})");
        _ = await Assert.That(report).Contains("assistant message (2.0s ago): line one\n  line two");
        _ = await Assert.That(rejected).StartsWith("error: child agent not found:");

        child.Activity.FinishTool(toolExecution);
        provider.Release();
        await registry.DisposeAsync();
    }

    [Test]
    public async Task Validates_strict_nonblank_input(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var router = TestModels.Route(new ProviderModel(provider, new LLMModel("model", provider.Id)));
        var resources = new UserSessionResources(
            new StatePaths(_root, _root, _root),
            UserSessionId.Parse(Guid.NewGuid().ToString("N")),
            ProjectWorkspace.FromLaunchDirectory(_root));
        var factory = new StatusAgentSessions(router, resources, TimeProvider.System);
        await using IAgentRegistry registry = new AgentRegistry(
            factory,
            _broker,
            _repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1024),
            TestDiagnosticLog.Instance,
            cancellationToken);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        await using var parentScope = factory.Build(
            AgentIdentity.Main("parent", "main", TestModels.PromptTemplates),
            AgentSessionParentLink.Root(),
            registry,
            status,
            _repository,
            _broker,
            cancellationToken);
        registry.RegisterRootScope(parentScope);
        var parent = parentScope.Session;
        ITool tool = new AgentStatusTool(
            new AgentResolver(parent.Identity, TestModels.ScopeOf(parent).ParentScope, TestModels.ScopeOf(parent), registry),
            TestModels.ScopeOf(parent).ParentScope);

        var blank = (await tool.Execute(
            new ToolInvocation("call", "{\"session_id\":\"  \"}"),
            new TurnFixture(parent, router).Selection,
            cancellationToken)).Text;
        var unknown = (await tool.Execute(
            new ToolInvocation("call", "{\"session_id\":\"child\",\"extra\":true}"),
            new TurnFixture(parent, router).Selection,
            cancellationToken)).Text;

        _ = await Assert.That(blank).IsEqualTo("error: no child session given");
        _ = await Assert.That(unknown).StartsWith("error:");
        await registry.DisposeAsync();
    }

    private sealed class TurnFixture
    {
        public TurnFixture(IAgentSession session, IModelRouter router)
        {
            var selection = session.CurrentSelection();
            Selection = new AgentTurnSelection(
                selection.RequestedModel,
                router.Resolve(selection.RequestedModel.Value),
                selection.Mode,
                selection.SecurityProfile);
        }

        public AgentTurnSelection Selection { get; }
    }

    private sealed class StatusAgentSessions(
        IModelRouter router,
        UserSessionResources resources,
        TimeProvider timeProvider) : IAgentSessionFactory
    {
        public IEventRepository PrepareHistory(string agentSessionId, IEventRepository repository) =>
            repository.BindAgentHistory(new AgentHistoryFile(resources, agentSessionId));

        public TestAgentSessionScope Build(
            AgentIdentity identity,
            AgentSessionParentLink parentLink,
            IAgentRegistry registry,
            IRuntimeStatus status,
            IEventRepository repository,
            IEventBroker broker,
            CancellationToken lifetime)
        {
            var scope = TestAgentSessionScope.BuildWithResources(
                identity,
                parentLink,
                registry,
                TestModels.PromptTemplates,
                resources,
                new ProcessRunner(string.Empty),
                TestDiagnosticLog.Instance,
                (sessionParentScope, owningScope, children, childQuestions) =>
                {
                    var exitReminder = new ExitReminder(repository, TestModels.PromptTemplates, identity.SessionId);
                    IAgentSession session = new AgentSession(identity, sessionParentScope, new ModelSelector(router.Resolve(string.Empty).RequestedSelector.Value), router, broker, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, new TestProfileFixture().Mode, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(children, identity), new ProcessActiveWorkBlocker(owningScope.Processes), new QueueActiveWorkBlocker(owningScope.GetService<IAgentQueues>(), TestModels.PromptTemplates)], TestModels.PromptTemplates), exitReminder, repository, broker).Callbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, status, new AgentSessionActivity(timeProvider), TestDiagnosticLog.Instance, lifetime);
                    return session;
                },
                lifetime);
            TestModels.RegisterScope(scope);
            return scope;
        }

        public IAgentSessionScope Create(
            AgentIdentity identity,
            AgentSessionParentLink parentLink,
            ModelSelector model,
            IEventBroker eventBroker,
            IEventRepository eventRepository,
            IMode mode,
            SecurityProfile securityProfile,
            IRuntimeStatus status,
            IAgentRegistry registry,
            CancellationToken lifetime) =>
            Build(identity, parentLink, registry, status, eventRepository, eventBroker, lifetime);
    }
}

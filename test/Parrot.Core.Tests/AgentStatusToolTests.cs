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
    private readonly EventBroker _broker = new();
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventRepository _repository;
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
        var processes = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), cancellationToken);
        using var queues = new AgentQueueCatalog(resources);
        var factory = new StatusAgentSessions(router, processes, queues, time);
        await using var registry = new AgentRegistry(
            factory,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var status = new RuntimeStatus(queues, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        await using var parentScope = factory.Build(
            AgentIdentity.Main("parent", "main", TestModels.PromptTemplates),
            registry,
            status,
            _repository,
            _broker,
            cancellationToken);
        registry.RegisterRootScope(parentScope);
        var parent = parentScope.Session;
        var child = registry.Spawn(Request(parent, router, "child"));
        var grandchild = registry.Spawn(Request(child, router, "grandchild"));
        _ = await child.Send("work", cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await grandchild.Send("work", cancellationToken);
        await provider.Arrived(cancellationToken);
        child.Activity.ObserveProviderEvent(LLMEvent.TextDelta("token"));
        child.Activity.RecordAssistantMessage("line one\nline two");
        time.Advance(TimeSpan.FromSeconds(2));
        var toolExecution = child.Activity.BeginTool("wait");
        var tool = new AgentStatusTool(registry, processes, parent);

        var report = (await tool.Execute(
            new ToolInvocation("call", "{\"session_id\":\"child\"}"),
            Turn(parent, router),
            cancellationToken)).Text;
        var rejected = (await tool.Execute(
            new ToolInvocation("call", $"{{\"session_id\":\"{grandchild.SessionId}\"}}"),
            Turn(parent, router),
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
        processes.Dispose();
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
        var processes = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), cancellationToken);
        using var queues = new AgentQueueCatalog(resources);
        var factory = new StatusAgentSessions(router, processes, queues, TimeProvider.System);
        await using var registry = new AgentRegistry(
            factory,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var status = new RuntimeStatus(queues, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        await using var parentScope = factory.Build(
            AgentIdentity.Main("parent", "main", TestModels.PromptTemplates),
            registry,
            status,
            _repository,
            _broker,
            cancellationToken);
        registry.RegisterRootScope(parentScope);
        var parent = parentScope.Session;
        var tool = new AgentStatusTool(registry, processes, parent);

        var blank = (await tool.Execute(
            new ToolInvocation("call", "{\"session_id\":\"  \"}"),
            Turn(parent, router),
            cancellationToken)).Text;
        var unknown = (await tool.Execute(
            new ToolInvocation("call", "{\"session_id\":\"child\",\"extra\":true}"),
            Turn(parent, router),
            cancellationToken)).Text;

        _ = await Assert.That(blank).IsEqualTo("error: no child session given");
        _ = await Assert.That(unknown).StartsWith("error:");
        await registry.DisposeAsync();
        processes.Dispose();
    }

    private static AgentLaunchRequest Request(AgentSession parent, ModelRouter router, string name) =>
        new(
            parent,
            Turn(parent, router),
            "worker",
            parent.Selection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            0,
            string.Empty,
            AgentCompletionDeliveryPolicy.RetainedOnly);

    private static AgentTurnSelection Turn(AgentSession session, ModelRouter router)
    {
        var selection = session.Selection();
        return new AgentTurnSelection(
            selection.RequestedModel,
            router.Resolve(selection.RequestedModel.Value),
            selection.Profile,
            selection.SecurityProfile);
    }

    private sealed class StatusAgentSessions(
        ModelRouter router,
        ShellProcessOwners processes,
        AgentQueueCatalog queueCatalog,
        TimeProvider timeProvider) : IAgentSessionFactory
    {
        public AgentSessionDirectScope Build(
            AgentIdentity identity,
            AgentRegistry registry,
            RuntimeStatus status,
            EventRepository repository,
            EventBroker broker,
            CancellationToken lifetime)
        {
            var processOwner = processes.Prepare(identity.SessionId);
            processes.Register(processOwner);
            var queues = queueCatalog.Register(identity);
            return AgentSessionDirectScope.Build(identity.SessionId, registry, TestModels.PromptTemplates, childQuestions =>
            {
                var session = new AgentSession(identity, new ModelSelector(router.Resolve(string.Empty).RequestedSelector.Value), router, broker, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new TodoCollection(identity.SessionId, repository, broker), new ToolOutputBlobStore(Path.GetTempPath()), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), TestModels.PromptTemplates, childQuestions, new ActiveWorkCompletionReminder(identity.SessionId, registry, processOwner, TestModels.PromptTemplates), TestModels.Profile(), SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), status, registry, queues, new AgentSessionActivity(timeProvider), lifetime);
                queues.Attach(session);
                return session;
            });
        }

        public IAgentSessionScope Create(
            AgentIdentity identity,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IMode mode,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            AgentRegistry registry,
            CancellationToken lifetime) =>
            Build(identity, registry, status, eventRepository, eventBroker, lifetime);
    }
}

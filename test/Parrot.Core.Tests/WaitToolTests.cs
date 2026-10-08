using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class WaitToolTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-wait-tool-tests", Guid.NewGuid().ToString("n"));
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly IEventBroker _broker = new EventBroker();
    private readonly List<IAgentRegistry> _registries = [];

    public WaitToolTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        foreach (var registry in _registries)
        {
            await registry.DisposeAsync().ConfigureAwait(false);
        }

        _broker.Dispose();
        _database.Dispose();
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task Schema_and_arguments_enforce_the_duration_contract(CancellationToken cancellationToken)
    {
        var time = new ManualTimeProvider();
        var provider = new UnusedProvider();
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], selectedRepository: null, registry);
        var session = scope.Session;
        var queues = scope.GetService<IAgentQueues>();
        _ = queues.Create("work", "queued work");
        _ = await queues.Push("work", ["item"], QueueDirection.Back, false, cancellationToken);
        _ = scope.GetService<IProcessOwner>().StartUnattributed("process", "sleep 60", ProcessEnvironmentOverrides.Empty, session, SecurityProfile.Compose(readOnly: false, [], [], []), ShellProcessTerminalMode.Pipe);
        using var childProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        await using var childScope = BuildSession(childProvider, [], new EventRepository(_database), registry, AgentIdentity.Child("child", AgentIdentity.Main("agent", "main", TestModels.PromptTemplates), "worker", 1, AgentScope.Empty(TestModels.PromptTemplates), AgentPolicyLineage.Root(), TestModels.PromptTemplates), AgentSessionParentLink.Child(scope, AgentCompletionDeliveryPolicy.RetainedOnly, registry.ReserveRetainedAgent()), QueueResources("agent"), TestDiagnosticLog.Instance);
        _ = await childScope.Session.Send([ConversationPart.TextPart("work")], "child-message", Delivery.Steer, new IncomingActivity(string.Empty, null), cancellationToken);
        await childProvider.Arrived(cancellationToken);
        var selection = new SelectionFixture(provider).Selection;
        Parrot.Tools.ITool tool = new Parrot.Tools.WaitTool(
            [new ChildAgentActiveWorkBlocker(scope.ChildRegistry, session.Identity), new ProcessActiveWorkBlocker(scope.GetService<IProcessOwner>()), new QueueActiveWorkBlocker(queues, TestModels.PromptTemplates)],
            session,
            time);

        var invalid = new[]
        {
            "[]",
            "{\"unexpected\":true}",
            "{\"duration_ms\":9999}",
            "{\"duration_ms\":-1}",
            "{\"duration_ms\":10000.5}",
            "{\"duration_ms\":\"10000\"}",
            "{\"duration_ms\":4294967295}",
        };

        foreach (var arguments in invalid)
        {
            _ = await Assert.That((await tool.Execute(new Parrot.Tools.ToolInvocation("test-call", arguments), new SelectionFixture(provider).Selection, cancellationToken)).Text).StartsWith("error:");
        }

        var waiting = tool.Execute(new Parrot.Tools.ToolInvocation("test-call", "{}"), selection, cancellationToken);
        await time.WaitForTimer(cancellationToken);
        time.Advance(TimeSpan.FromMinutes(5));
        _ = await Assert.That((await waiting).Text).IsEqualTo(
            $"""
            Wait timed out after 300000 ms.
            Running direct subagents:
            - {childScope.Session.SessionId} (name: worker)
            Running processes:
            - agent/process (name: process)
            """);
    }

    [Test]
    public async Task Pending_input_wakes_a_new_wait(CancellationToken cancellationToken)
    {
        var repository = new EventRepository(_database);
        var provider = new UnusedProvider();
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], repository, registry);
        var session = scope.Session;
        Parrot.Tools.ITool tool = new Parrot.Tools.WaitTool(
            [],
            session,
            TimeProvider.System);
        _ = repository.Admit(
            session.SessionId,
            "pending",
            "already here",
            Delivery.Steer,
            static input => new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = "agent",
                InputAdmitted = new InputAdmitted { InputId = input.Id, MessageId = input.MessageId },
            });

        _ = await Assert.That((await tool.Execute(new Parrot.Tools.ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, cancellationToken)).Text).IsEqualTo("wait interrupted");
    }

    [Test]
    public async Task Agent_completion_identifies_the_agent_that_interrupted_wait(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], selectedRepository: null, registry);
        var session = scope.Session;
        Parrot.Tools.ITool tool = new Parrot.Tools.WaitTool(
            [],
            session,
            TimeProvider.System);
        var waiting = tool.Execute(new Parrot.Tools.ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, cancellationToken);

        _ = await session.Send(
            [ConversationPart.TextPart("completed")],
            Identifier.MessageId(),
            Delivery.Steer,
            new IncomingActivity("researcher", "researcher completion"),
            cancellationToken);

        _ = await Assert.That((await waiting).Text)
            .IsEqualTo("wait interrupted due to researcher completion");
    }

    [Test]
    public async Task AgentTask_completion_identifies_the_graph_that_interrupted_wait(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var repository = new EventRepository(_database);
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], repository, registry);
        var session = scope.Session;
        Parrot.Tools.ITool tool = new Parrot.Tools.WaitTool(
            [],
            session,
            TimeProvider.System);
        var waiting = tool.Execute(new Parrot.Tools.ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, cancellationToken);

        _ = await session.Send(
            [ConversationPart.TextPart("completed")],
            Identifier.MessageId(),
            Delivery.Steer,
            new IncomingActivity("graph-call", "AgentTask graph graph-call completion"),
            cancellationToken);

        _ = await Assert.That((await waiting).Text)
            .IsEqualTo("wait interrupted due to AgentTask graph graph-call completion");
        var admitted = repository.Replay().Single(published =>
            published.AgentSessionId == session.SessionId
            && published.PayloadCase == Event.PayloadOneofCase.InputAdmitted);
        _ = await Assert.That(admitted.InputAdmitted.MessageId).IsNotEmpty();
        _ = await Assert.That(admitted.InputAdmitted.Content).IsEqualTo("completed");
    }

    [Test]
    public async Task AgentTask_notifications_spill_oversized_text(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var repository = new EventRepository(_database);
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], repository, registry);
        var session = scope.Session;
        var notifier = new AgentTaskNotifier(
            scope,
            new ToolOutputBlobStore(Path.Combine(_root, "agent-task-blobs")),
            TestDiagnosticLog.Instance);

        notifier.Enqueue(new string('r', ToolOutputBlobStore.MaximumInlineBytes + 1), "AgentTask update", false);
        await notifier.Settle();

        var input = repository.Replay().Single(published =>
            published.AgentSessionId == session.SessionId
            && published.PayloadCase == Event.PayloadOneofCase.InputAdmitted).InputAdmitted;
        _ = await Assert.That(input.Content).Contains("Tool output exceeded 64 KiB and was saved to ");
        var notice = input.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(line => line.StartsWith("Tool output exceeded", StringComparison.Ordinal));
        var path = notice[(notice.IndexOf("saved to ", StringComparison.Ordinal) + "saved to ".Length)..]
            .TrimEnd('.');
        _ = await Assert.That((await File.ReadAllTextAsync(path, cancellationToken)).Length)
            .IsEqualTo(ToolOutputBlobStore.MaximumInlineBytes + 1);
    }

    [Test]
    public async Task Process_completion_identifies_the_process_that_interrupted_wait(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], selectedRepository: null, registry);
        var session = scope.Session;
        Parrot.Tools.ITool tool = new Parrot.Tools.WaitTool(
            [],
            session,
            TimeProvider.System);
        var waiting = tool.Execute(new Parrot.Tools.ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, cancellationToken);

        _ = await session.Send(
            [ConversationPart.TextPart("completed")],
            Identifier.MessageId(),
            Delivery.Steer,
            new IncomingActivity("compiler", "process compiler completion"),
            cancellationToken);

        _ = await Assert.That((await waiting).Text)
            .IsEqualTo("wait interrupted due to process compiler completion");
    }

    [Test]
    public async Task Cancellation_clears_the_wait_registration(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], selectedRepository: null, registry);
        var session = scope.Session;
        Parrot.Tools.ITool tool = new Parrot.Tools.WaitTool(
            [],
            session,
            TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        var waiting = tool.Execute(new Parrot.Tools.ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, canceled.Token);
        await canceled.CancelAsync();

        _ = await Assert.That(waiting).Throws<OperationCanceledException>();
        var afterCancel = await session.WaitForIncomingInput(
            TimeSpan.FromSeconds(1),
            TimeProvider.System,
            cancellationToken);
        _ = await Assert.That(afterCancel is null).IsTrue();
    }

    [Test]
    public async Task A_wait_tool_round_promotes_the_waking_message_once(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("tool_calls", 1, 0, 1, string.Empty, [new LLMToolCall("wait-call", "wait", "{}")]),
            LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var repository = new EventRepository(_database);
        var registry = PrepareRegistry(new EventRepository(_database));
        var factory = new Parrot.Tools.WaitToolFactory([], TimeProvider.System, TestModels.ToolDefinitions);
        await using var scope = Session(provider, [factory], repository, registry);
        var session = scope.Session;

        _ = await session.Send(
            [ConversationPart.TextPart("first")], "message-1", Delivery.Steer, new IncomingActivity(string.Empty, null), cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await TestPolling.Until(() => IsWaiting(session), cancellationToken);
        _ = await session.Send(
            [ConversationPart.TextPart("first")], "message-1", Delivery.Steer, new IncomingActivity(string.Empty, null), cancellationToken);
        await Task.Delay(20, cancellationToken);
        _ = await Assert.That(IsWaiting(session)).IsTrue();
        _ = await session.Send(
            [ConversationPart.TextPart("wakeup")], "message-2", Delivery.Steer, new IncomingActivity(string.Empty, null), cancellationToken);
        await provider.Arrived(cancellationToken);

        var request = provider.Requests[1];
        _ = await Assert.That(request.Messages.Count(message => message.Content == "wakeup")).IsEqualTo(1);
        _ = await Assert.That(request.Messages.Single(message => message.Role == LLMRole.Tool).Content)
            .IsEqualTo("wait interrupted");
        _ = await Assert.That(session.Activity.Capture().State).IsEqualTo(DrainState.Running);

        provider.Release();
        await session.DisposeAsync();
        _ = await Assert.That(session.Activity.Capture().State).IsEqualTo(DrainState.Idle);
        var lifecycle = repository.Replay().Where(published => published.PayloadCase is
            Event.PayloadOneofCase.ToolStarted or Event.PayloadOneofCase.ToolFinished).ToArray();
        _ = await Assert.That(lifecycle.Length).IsEqualTo(2);
    }

    private static bool IsWaiting(IAgentSession session) => session.Activity.Capture().State != DrainState.Idle;

    private UserSessionResources QueueResources(string sessionId) => new(
        new StatePaths(_root, Path.Combine(_root, "config"), Path.Combine(_root, "data")),
        UserSessionId.Parse(sessionId),
        ProjectWorkspace.FromLaunchDirectory(_root));

    private TestAgentSessionScope Session(
        ILLMProvider provider,
        IReadOnlyList<Parrot.Tools.IToolFactory> tools,
        IEventRepository? selectedRepository,
        IAgentRegistry registry) =>
        BuildSession(provider, tools, selectedRepository ?? new EventRepository(_database), registry, AgentIdentity.Main("agent", "main", TestModels.PromptTemplates), AgentSessionParentLink.Root(), QueueResources("agent"), TestDiagnosticLog.Instance);

    private TestAgentSessionScope BuildSession(
        ILLMProvider provider,
        IReadOnlyList<Parrot.Tools.IToolFactory> tools,
        IEventRepository repository,
        IAgentRegistry registry,
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        UserSessionResources resources,
        IDiagnosticLog diagnostics)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var scope = TestAgentSessionScope.BuildWithResources(
            identity,
            parentLink,
            registry,
            TestModels.PromptTemplates,
            resources,
            TestModels.Runner(SandboxPassThrough.Write(_root)),
            diagnostics,
            (sessionParentScope, owningScope, children, childQuestions) =>
            {
                var exitReminder = new ExitReminder(repository, _broker, TestModels.PromptTemplates, identity.SessionId);
                IAgentSession session = new AgentSession(identity, sessionParentScope, new ModelSelector(model.Selector), TestModels.Route(model), _broker, repository, tools, TestModels.MaterializePrompt(identity, _root, _root), new ToolOutputBlobStore(Path.Combine(_root, "blobs")), new AgentOutputFile(Path.Combine(_root, "blobs")), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, new TestProfileFixture().Profile, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(children, identity), new ProcessActiveWorkBlocker(owningScope.GetService<IProcessOwner>()), new QueueActiveWorkBlocker(owningScope.GetService<IAgentQueues>(), TestModels.PromptTemplates)], TestModels.PromptTemplates), exitReminder, repository, _broker).Callbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, TestModels.ScopedRuntimeStatus(registry, owningScope), new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, CancellationToken.None);
                return session;
            },
            CancellationToken.None);
        if (parentLink.Parent is { } parent)
        {
            _ = parent.ChildRegistry.TryAdd(scope);
        }
        else
        {
            registry.RegisterRootScope(scope);
        }

        return scope;
    }

    private IAgentRegistry PrepareRegistry(IEventRepository repository)
    {
        IAgentRegistry registry = new AgentRegistry(
            new UnsupportedAgentSessionFactory(),
            _broker,
            repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1024),
            TestDiagnosticLog.Instance,
            CancellationToken.None);
        _registries.Add(registry);
        return registry;
    }

    private sealed class SelectionFixture
    {
        public SelectionFixture(UnusedProvider provider)
        {
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            Selection = new AgentTurnSelection(
                new ModelSelector(model.Selector),
                TestModels.Resolve(model),
                new TestProfileFixture().Profile,
                SecurityProfile.Compose(readOnly: false, [], [], []));
        }

        public AgentTurnSelection Selection { get; }
    }

    private sealed class ManualTimeProvider : TimeProvider
    {
        private readonly Lock _gate = new();
        private readonly List<ManualTimer> _timers = [];
        private readonly TaskCompletionSource _timerCreated = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private DateTimeOffset _utcNow = DateTimeOffset.UnixEpoch;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new ManualTimer(this, callback, state, _utcNow + dueTime);
            lock (_gate)
            {
                _timers.Add(timer);
            }

            _ = _timerCreated.TrySetResult();
            return timer;
        }

        public async Task WaitForTimer(CancellationToken cancellationToken) =>
            await _timerCreated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        public void Advance(TimeSpan duration)
        {
            ManualTimer[] due;
            lock (_gate)
            {
                _utcNow += duration;
                due = [.. _timers.Where(timer => !timer.Disposed && timer.Due <= _utcNow)];
            }

            foreach (var timer in due)
            {
                timer.Fire();
            }
        }

        private sealed class ManualTimer(
            ManualTimeProvider owner,
            TimerCallback callback,
            object? state,
            DateTimeOffset due) : ITimer
        {
            public DateTimeOffset Due { get; private set; } = due;

            public bool Disposed { get; private set; }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                Due = owner._utcNow + dueTime;
                return !Disposed;
            }

            public void Dispose() => Disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                if (!Disposed)
                {
                    callback(state);
                }
            }
        }
    }

    private sealed class UnsupportedAgentSessionFactory : IAgentSessionFactory
    {
        public IEventRepository PrepareHistory(AgentIdentity identity, IEventRepository repository) =>
            throw new NotSupportedException("This test session does not support spawning subagents.");

        public IAgentSessionScope Create(
            AgentIdentity identity,
            AgentSessionParentLink parentLink,
            ModelSelector model,
            IEventBroker eventBroker,
            IEventRepository eventRepository,
            IAgentProfile profile,
            SecurityProfile securityProfile,
            IAgentRegistry registry,
            CancellationToken lifetime) =>
            throw new NotSupportedException("This test session does not support spawning subagents.");
    }
}

using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;
using AgentUserSession = Parrot.Agent.UserSession;

namespace Parrot.Core.Tests;

internal sealed class WaitToolTests : IAsyncDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-wait-tool-tests", Guid.NewGuid().ToString("n"));
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();
    private readonly List<Parrot.Process.ShellProcessOwners> _processOwners = [];
    private readonly List<AgentRegistry> _registries = [];
    private readonly List<ChildQuestionCoordinator> _childQuestions = [];

    public WaitToolTests() => Directory.CreateDirectory(_root);

    public async ValueTask DisposeAsync()
    {
        foreach (var childQuestions in _childQuestions)
        {
            childQuestions.Dispose();
        }

        foreach (var registry in _registries)
        {
            await registry.DisposeAsync().ConfigureAwait(false);
        }

        foreach (var processes in _processOwners)
        {
            processes.Dispose();
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
        using var queueCatalog = QueueCatalog("schema-queues");
        using var queues = queueCatalog.Register(AgentIdentity.Main("agent", "main", TestModels.PromptTemplates));
        var session = Session(provider, [], selectedRepository: null, queueCatalog, queues);
        _ = queues.Create("work", "queued work");
        _ = await queues.Push("work", ["item"], QueueDirection.Back, false, cancellationToken);
        var processes = new ProcessStatusSource(
            new ShellProcessStatusSnapshot("agent", "process", "process", ActiveWorkState.Running));
        var subagents = new AgentStatusSource(
            new ActiveAgentSnapshot("child", "agent", "worker"));
        var tool = new WaitTool(
            new RuntimeStatus(queueCatalog, processes, subagents, TestModels.PromptTemplates, TimeProvider.System),
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
            _ = await Assert.That((await tool.Execute(new ToolInvocation("test-call", arguments), Selection(provider), cancellationToken)).Text).StartsWith("error:");
        }

        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), Selection(provider), cancellationToken);
        await time.WaitForTimer(cancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        _ = await Assert.That((await waiting).Text).IsEqualTo(
            """
            Wait timed out after 10000 ms.

            Runtime:
            - agent: main (agent)
              - queue: work (1 items, description: "queued work")
              - process: agent/process (shell, running, name: process)
              - agent: worker (child)
            """);
    }

    [Test]
    public async Task Pending_input_wakes_a_new_wait(CancellationToken cancellationToken)
    {
        var repository = new EventRepository(_database);
        var provider = new UnusedProvider();
        using var queueCatalog = QueueCatalog("pending-queues");
        using var queues = queueCatalog.Register(AgentIdentity.Main("agent", "main", TestModels.PromptTemplates));
        var session = Session(provider, [], repository, queueCatalog, queues);
        var unobservedProcesses = new UnobservedProcessStatusSource();
        var unobservedAgents = new UnobservedAgentStatusSource();
        var tool = new WaitTool(
            new RuntimeStatus(queueCatalog, unobservedProcesses, unobservedAgents, TestModels.PromptTemplates, TimeProvider.System),
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

        _ = await Assert.That((await tool.Execute(new ToolInvocation("test-call", "{}"), Selection(provider), cancellationToken)).Text).IsEqualTo("wait interrupted");
    }

    [Test]
    public async Task Agent_completion_identifies_the_agent_that_interrupted_wait(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        using var queueCatalog = QueueCatalog("agent-completion-queues");
        using var queues = queueCatalog.Register(AgentIdentity.Main("agent", "main", TestModels.PromptTemplates));
        var session = Session(provider, [], selectedRepository: null, queueCatalog, queues);
        var tool = new WaitTool(
            new RuntimeStatus(queueCatalog, new UnobservedProcessStatusSource(), new UnobservedAgentStatusSource(), TestModels.PromptTemplates, TimeProvider.System),
            session,
            TimeProvider.System);
        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), Selection(provider), cancellationToken);
        await WaitUntil(session.IsWaitingForIncomingInput, cancellationToken);

        await session.ReceiveAgentCompletion("researcher", "completed", cancellationToken);

        _ = await Assert.That((await waiting).Text)
            .IsEqualTo("wait interrupted due to researcher completion");
    }

    [Test]
    public async Task Process_completion_identifies_the_process_that_interrupted_wait(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        using var queueCatalog = QueueCatalog("process-completion-queues");
        using var queues = queueCatalog.Register(AgentIdentity.Main("agent", "main", TestModels.PromptTemplates));
        var session = Session(provider, [], selectedRepository: null, queueCatalog, queues);
        var tool = new WaitTool(
            new RuntimeStatus(queueCatalog, new UnobservedProcessStatusSource(), new UnobservedAgentStatusSource(), TestModels.PromptTemplates, TimeProvider.System),
            session,
            TimeProvider.System);
        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), Selection(provider), cancellationToken);
        await WaitUntil(session.IsWaitingForIncomingInput, cancellationToken);

        await session.ReceiveProcessCompletion(
            "compiler",
            "completed",
            Identifier.MessageId(),
            cancellationToken);

        _ = await Assert.That((await waiting).Text)
            .IsEqualTo("wait interrupted due to process compiler completion");
    }

    [Test]
    public async Task Cancellation_clears_the_wait_registration(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        using var queueCatalog = QueueCatalog("cancellation-queues");
        using var queues = queueCatalog.Register(AgentIdentity.Main("agent", "main", TestModels.PromptTemplates));
        var session = Session(provider, [], selectedRepository: null, queueCatalog, queues);
        var unobservedProcesses = new UnobservedProcessStatusSource();
        var unobservedAgents = new UnobservedAgentStatusSource();
        var tool = new WaitTool(
            new RuntimeStatus(queueCatalog, unobservedProcesses, unobservedAgents, TestModels.PromptTemplates, TimeProvider.System),
            session,
            TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), Selection(provider), canceled.Token);
        await WaitUntil(session.IsWaitingForIncomingInput, cancellationToken);

        await canceled.CancelAsync();

        _ = await Assert.That(waiting).Throws<OperationCanceledException>();
        _ = await Assert.That(session.IsWaitingForIncomingInput()).IsFalse();
    }

    [Test]
    public async Task A_wait_tool_round_promotes_the_waking_message_once(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("tool_calls", 1, 0, 1, string.Empty, [new LLMToolCall("wait-call", "wait", "{}")]),
            LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var repository = new EventRepository(_database);
        using var queueCatalog = QueueCatalog("round-queues");
        using var queues = queueCatalog.Register(AgentIdentity.Main("agent", "main", TestModels.PromptTemplates));
        var processes = new ProcessStatusSource();
        var agents = new AgentStatusSource();
        var factory = new WaitToolFactory(new RuntimeStatus(queueCatalog, processes, agents, TestModels.PromptTemplates, TimeProvider.System), TimeProvider.System);
        var session = Session(provider, [factory], repository, queueCatalog, queues);

        _ = await session.Admit("first", "message-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await WaitUntil(session.IsWaitingForIncomingInput, cancellationToken);
        _ = await session.Admit("first", "message-1", Delivery.Steer, cancellationToken);
        await Task.Delay(20, cancellationToken);
        _ = await Assert.That(session.IsWaitingForIncomingInput()).IsTrue();
        _ = await session.Admit("wakeup", "message-2", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);

        var request = provider.Requests[1];
        _ = await Assert.That(request.Messages.Count(message => message.Content == "wakeup")).IsEqualTo(1);
        _ = await Assert.That(request.Messages.Single(message => message.Role == LLMRole.Tool).Content)
            .IsEqualTo("wait interrupted");
        _ = await Assert.That(session.State).IsEqualTo(DrainState.Running);

        provider.Release();
        await session.Settled();
        _ = await Assert.That(session.State).IsEqualTo(DrainState.Idle);
        var lifecycle = repository.Replay().Where(published => published.PayloadCase is
            Event.PayloadOneofCase.ToolStarted or Event.PayloadOneofCase.ToolFinished).ToArray();
        _ = await Assert.That(lifecycle.Length).IsEqualTo(2);
    }

    [Test]
    public async Task Child_listener_wakes_and_drains_a_direct_parents_queue(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var repository = new EventRepository(_database);
        using var queueCatalog = QueueCatalog("parent-listener-queues");
        using var parentQueues = queueCatalog.Register(AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates));
        using var childQueues = queueCatalog.Register(
            AgentIdentity.Child("child", "parent", "parent", "child", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        var child = Session(
            provider,
            [],
            repository,
            queueCatalog,
            childQueues,
            AgentIdentity.Child("child", "parent", "parent", "child", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        _ = parentQueues.Create("parent-work", string.Empty);
        _ = await childQueues.Listen("parent-work", true, cancellationToken);
        var waiting = child.WaitForIncomingInput(TimeSpan.FromMinutes(1), TimeProvider.System, cancellationToken);
        await WaitUntil(child.IsWaitingForIncomingInput, cancellationToken);

        _ = await parentQueues.Push("parent-work", ["from-parent"], QueueDirection.Back, false, cancellationToken);

        _ = await Assert.That((await waiting)?.Kind).IsEqualTo(IncomingActivityKind.Input);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(string.Join('\n', provider.Requests.Single().Messages.Select(message => message.Content)))
            .Contains("Queue notification from \"parent-work\":\n\nfrom-parent");
        _ = await Assert.That(parentQueues.Get("parent-work").Size).IsEqualTo(0);
        provider.Release();
        await child.Settled();
    }

    [Test]
    public async Task Competing_listeners_deliver_one_item_to_only_one_wait(CancellationToken cancellationToken)
    {
        using var firstProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        using var secondProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var repository = new EventRepository(_database);
        using var queueCatalog = QueueCatalog("competing-listener-queues");
        using var parentQueues = queueCatalog.Register(AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates));
        using var firstQueues = queueCatalog.Register(
            AgentIdentity.Child("first-child", "parent", "parent", "first", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        using var secondQueues = queueCatalog.Register(
            AgentIdentity.Child("second-child", "parent", "parent", "second", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        var first = Session(
            firstProvider,
            [],
            repository,
            queueCatalog,
            firstQueues,
            AgentIdentity.Child("first-child", "parent", "parent", "first", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        var second = Session(
            secondProvider,
            [],
            repository,
            queueCatalog,
            secondQueues,
            AgentIdentity.Child("second-child", "parent", "parent", "second", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        _ = parentQueues.Create("shared-work", string.Empty);
        _ = await firstQueues.Listen("shared-work", true, cancellationToken);
        _ = await secondQueues.Listen("shared-work", true, cancellationToken);
        using var firstWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var secondWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var firstWaiting = first.WaitForIncomingInput(
            TimeSpan.FromMinutes(1), TimeProvider.System, firstWaitCancellation.Token);
        var secondWaiting = second.WaitForIncomingInput(
            TimeSpan.FromMinutes(1), TimeProvider.System, secondWaitCancellation.Token);
        await WaitUntil(
            () => first.IsWaitingForIncomingInput() && second.IsWaitingForIncomingInput(),
            cancellationToken);

        _ = await parentQueues.Push("shared-work", ["single-item"], QueueDirection.Back, false, cancellationToken);
        _ = await Task.WhenAll(
            firstQueues.Deliver(cancellationToken),
            secondQueues.Deliver(cancellationToken));

        _ = await Assert.That(firstWaiting.IsCompleted ^ secondWaiting.IsCompleted).IsTrue();
        if (firstWaiting.IsCompleted)
        {
            await firstProvider.Arrived(cancellationToken);
        }
        else
        {
            await secondProvider.Arrived(cancellationToken);
        }

        _ = await Assert.That(firstProvider.Requests.Count + secondProvider.Requests.Count).IsEqualTo(1);
        _ = await Assert.That(parentQueues.Get("shared-work").Size).IsEqualTo(0);

        if (firstWaiting.IsCompleted)
        {
            _ = await Assert.That((await firstWaiting)?.Kind).IsEqualTo(IncomingActivityKind.Input);
            await secondWaitCancellation.CancelAsync();
            _ = await Assert.That(secondWaiting).Throws<OperationCanceledException>();
            firstProvider.Release();
            await first.Settled();
        }
        else
        {
            _ = await Assert.That((await secondWaiting)?.Kind).IsEqualTo(IncomingActivityKind.Input);
            await firstWaitCancellation.CancelAsync();
            _ = await Assert.That(firstWaiting).Throws<OperationCanceledException>();
            secondProvider.Release();
            await second.Settled();
        }
    }

    [Test]
    public async Task Disabling_one_listener_leaves_another_listener_active(CancellationToken cancellationToken)
    {
        using var disabledProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        using var activeProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var repository = new EventRepository(_database);
        using var queueCatalog = QueueCatalog("disabled-listener-queues");
        using var parentQueues = queueCatalog.Register(AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates));
        using var disabledQueues = queueCatalog.Register(
            AgentIdentity.Child("disabled-child", "parent", "parent", "disabled", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        using var activeQueues = queueCatalog.Register(
            AgentIdentity.Child("active-child", "parent", "parent", "active", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        var disabled = Session(
            disabledProvider,
            [],
            repository,
            queueCatalog,
            disabledQueues,
            AgentIdentity.Child("disabled-child", "parent", "parent", "disabled", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        var active = Session(
            activeProvider,
            [],
            repository,
            queueCatalog,
            activeQueues,
            AgentIdentity.Child("active-child", "parent", "parent", "active", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates));
        _ = parentQueues.Create("shared-work", string.Empty);
        _ = await disabledQueues.Listen("shared-work", true, cancellationToken);
        _ = await activeQueues.Listen("shared-work", true, cancellationToken);
        _ = await disabledQueues.Listen("shared-work", false, cancellationToken);
        using var disabledWaitCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var disabledWaiting = disabled.WaitForIncomingInput(
            TimeSpan.FromMinutes(1), TimeProvider.System, disabledWaitCancellation.Token);
        var activeWaiting = active.WaitForIncomingInput(TimeSpan.FromMinutes(1), TimeProvider.System, cancellationToken);
        await WaitUntil(
            () => disabled.IsWaitingForIncomingInput() && active.IsWaitingForIncomingInput(),
            cancellationToken);

        _ = await parentQueues.Push("shared-work", ["active-item"], QueueDirection.Back, false, cancellationToken);
        _ = await activeQueues.Deliver(cancellationToken);

        _ = await Assert.That((await activeWaiting)?.Kind).IsEqualTo(IncomingActivityKind.Input);
        await activeProvider.Arrived(cancellationToken);
        _ = await Assert.That(disabledWaiting.IsCompleted).IsFalse();
        _ = await Assert.That(disabledProvider.Requests).IsEmpty();
        _ = await Assert.That(string.Join('\n', activeProvider.Requests.Single().Messages.Select(message => message.Content)))
            .Contains("Queue notification from \"shared-work\":\n\nactive-item");
        _ = await Assert.That(parentQueues.Get("shared-work").Size).IsEqualTo(0);
        await disabledWaitCancellation.CancelAsync();
        _ = await Assert.That(disabledWaiting).Throws<OperationCanceledException>();
        activeProvider.Release();
        await active.Settled();
    }

    [Test]
    public async Task Listened_queue_wakes_wait_but_unlistened_queue_does_not(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("tool_calls", 1, 0, 1, string.Empty, [new LLMToolCall("wait-call", "wait", "{}")]),
            LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var router = TestModels.Route(model);
        var sessions = new WaitAgentSessions(router, TimeProvider.System, _root);
        var profiles = TestModels.ProfileRegistry();
        var modes = new ModeRegistry(profiles, ModeRegistry.Build);
        await using var owner = new AgentUserSession(
            "user",
            "main",
            router.Resolve(model.Selector),
            "build",
            Resources("user"),
            sessions,
            new UserSessionModes(modes, TestModels.PromptTemplates, Path.Combine(_root, "plans")),
            TestModels.PromptTemplates,
            profiles,
            interactivePermissions: false,
            TimeSpan.FromSeconds(1),
            TimeProvider.System);

        _ = await owner.Send("first", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        var session = await sessions.WaitForSession(cancellationToken);
        _ = session.Queues.Create("ignored", string.Empty);
        _ = await session.Queues.Push("ignored", ["no wake"], QueueDirection.Back, false, cancellationToken);
        _ = session.Queues.Create("work", string.Empty);
        _ = await session.Queues.Listen("work", true, cancellationToken);
        _ = await session.Queues.Push("work", ["queued"], QueueDirection.Back, false, cancellationToken);
        _ = await Assert.That(session.Queues.Get("work").Size).IsEqualTo(1);

        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(string.Join('\n', provider.Requests[1].Messages.Select(message => message.Content)))
            .Contains("Queue notification from \"work\":\n\nqueued");
        _ = await Assert.That(session.Queues.Get("work").Size).IsEqualTo(0);
        _ = await Assert.That(session.Queues.Get("ignored").Size).IsEqualTo(1);
        provider.Release();
    }

    private static async Task WaitUntil(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static AgentTurnSelection Selection(UnusedProvider provider)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        return new AgentTurnSelection(
            new ModelSelector(model.Selector),
            TestModels.Resolve(model),
            TestModels.Profile(),
            SecurityProfile.Compose(readOnly: false, [], [], []));
    }

    private SessionResourceLease Resources(string ownerId) => SessionResourceLease.Own(
        UserSessionResources(ownerId),
        _database);

    private AgentQueueCatalog QueueCatalog(string ownerId) => new(UserSessionResources(ownerId));

    private UserSessionResources UserSessionResources(string ownerId) => new(
        new StatePaths(_root, Path.Combine(_root, "config"), Path.Combine(_root, "data")),
        UserSessionId.Parse(ownerId),
        ProjectWorkspace.FromLaunchDirectory(_root));

    private AgentSession Session(
        ILLMProvider provider,
        IReadOnlyList<IToolFactory> tools,
        EventRepository? selectedRepository,
        AgentQueueCatalog queueCatalog,
        AgentQueues queues) =>
        Session(provider, tools, selectedRepository, queueCatalog, queues, AgentIdentity.Main("agent", "main", TestModels.PromptTemplates));

    private AgentSession Session(
        ILLMProvider provider,
        IReadOnlyList<IToolFactory> tools,
        EventRepository? selectedRepository,
        AgentQueueCatalog queueCatalog,
        AgentQueues queues,
        AgentIdentity identity)
    {
        var repository = selectedRepository ?? new EventRepository(_database);
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var resources = UserSessionResources(identity.SessionId);
        var processes = PrepareProcesses(resources);
        var processOwner = processes.Prepare(identity.SessionId);
        processes.Register(processOwner);
        var registry = PrepareRegistry(repository);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        var children = new ChildRegistry(identity, registry);
        var childQuestions = new ChildQuestionCoordinator(children, TestModels.PromptTemplates);
        _childQuestions.Add(childQuestions);
        var session = new AgentSession(identity, new ModelSelector(model.Selector), TestModels.Route(model), _broker, repository, tools, tools.Count == 0 ? TestModels.EmptyToolDefinitions : TestModels.DocumentTools("wait"), TestModels.MaterializePrompt(identity, _root, _root), new TodoCollection("agent", repository, _broker), new ToolOutputBlobStore(Path.Combine(_root, "blobs")), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), TestModels.PromptTemplates, childQuestions, new ActiveWorkCompletionReminder(children, processOwner, TestModels.PromptTemplates), TestModels.Profile(), SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), status, registry, children, queues, new AgentSessionActivity(TimeProvider.System), CancellationToken.None);
        queues.Attach(session);
        return session;
    }

    private Parrot.Process.ShellProcessOwners PrepareProcesses(UserSessionResources resources)
    {
        var processes = new Parrot.Process.ShellProcessOwners(
            resources,
            new Parrot.Process.ProcessRunner(string.Empty),
            CancellationToken.None);
        _processOwners.Add(processes);
        return processes;
    }

    private AgentRegistry PrepareRegistry(EventRepository repository)
    {
        var registry = new AgentRegistry(
            new UnsupportedAgentSessionFactory(),
            _broker,
            repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            CancellationToken.None);
        _registries.Add(registry);
        return registry;
    }

    private sealed class ProcessStatusSource(params ShellProcessStatusSnapshot[] active) : IProcessStatusSource
    {
        public IReadOnlyList<ShellProcessStatusSnapshot> Snapshot() => active;
    }

    private sealed class AgentStatusSource(params ActiveAgentSnapshot[] active) : IAgentStatusSource
    {
        public IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot() => active;
    }

    private sealed class UnobservedProcessStatusSource : IProcessStatusSource
    {
        public IReadOnlyList<ShellProcessStatusSnapshot> Snapshot() =>
            throw new InvalidOperationException("Process status was observed before timeout.");
    }

    private sealed class UnobservedAgentStatusSource : IAgentStatusSource
    {
        public IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot() =>
            throw new InvalidOperationException("Agent status was observed before timeout.");
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
        public IAgentSessionScope Create(
            AgentIdentity identity,
            AgentSessionParentScope parentScope,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IMode mode,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            AgentRegistry registry,
            CancellationToken lifetime) =>
            throw new NotSupportedException("This test session does not support spawning subagents.");
    }

    private sealed class WaitAgentSessions(ModelRouter router, TimeProvider timeProvider, string root) : IAgentSessionFactorySource
    {
        private readonly TaskCompletionSource<AgentSession> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAgentSessionFactory Create(AgentUserSession owner) => new Factory(this, owner, router, timeProvider, root);

        public Parrot.Process.ShellProcessOwners CreateShellProcesses(AgentUserSession owner) =>
            new(owner.Resources, new Parrot.Process.ProcessRunner(string.Empty), owner.Lifetime);

        public Parrot.Queues.AgentQueueCatalog CreateQueueCatalog(AgentUserSession owner) =>
            new(owner.Resources);

        public async Task<AgentSession> WaitForSession(CancellationToken cancellationToken) =>
            await _created.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        private sealed class Factory(
            WaitAgentSessions source,
            AgentUserSession owner,
            ModelRouter router,
            TimeProvider timeProvider,
            string root) : IAgentSessionFactory
        {
            public IAgentSessionScope Create(
                AgentIdentity identity,
                AgentSessionParentScope parentScope,
                ModelSelector model,
                EventBroker eventBroker,
                EventRepository eventRepository,
                IMode mode,
                SecurityProfile securityProfile,
                Parrot.Statuses.RuntimeStatus status,
                AgentRegistry registry,
                CancellationToken lifetime)
            {
                var processes = owner.ShellProcesses.Prepare(identity.SessionId);
                owner.ShellProcesses.Register(processes);
                var queues = owner.QueueCatalog.Register(identity);
                return AgentSessionDirectScope.Build(identity, registry, TestModels.PromptTemplates, (children, childQuestions) =>
                {
                    var session = new AgentSession(identity, model, router, eventBroker, eventRepository, [new WaitToolFactory(status, timeProvider)], TestModels.DocumentTools("wait"), TestModels.MaterializePrompt(identity, root, root), new TodoCollection(identity.SessionId, eventRepository, eventBroker), new ToolOutputBlobStore(Path.Combine(root, "blobs")), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), TestModels.PromptTemplates, childQuestions, new ActiveWorkCompletionReminder(children, processes, TestModels.PromptTemplates), mode, SecurityProfileTestFactory.Create(securityProfile), status, registry, children, queues, new AgentSessionActivity(TimeProvider.System), lifetime);
                    queues.Attach(session);
                    _ = source._created.TrySetResult(session);
                    return session;
                });
            }
        }
    }
}

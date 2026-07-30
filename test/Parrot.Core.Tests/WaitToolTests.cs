using System.Text.Json;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.State;
using Parrot.Store;
using Parrot.Tools;
using AgentUserSession = Parrot.Agent.UserSession;

namespace Parrot.Core.Tests;

internal sealed class WaitToolTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-wait-tool-tests", Guid.NewGuid().ToString("n"));
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();

    public WaitToolTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
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
        var tool = new WaitTool(Session(new UnusedProvider(), owner: null, [], selectedRepository: null), time);
        using var schema = JsonDocument.Parse(tool.ParametersJson);
        var duration = schema.RootElement.GetProperty("properties").GetProperty("duration_ms");

        _ = await Assert.That(duration.GetProperty("minimum").GetInt64()).IsEqualTo(10_000);
        _ = await Assert.That(duration.GetProperty("default").GetInt64()).IsEqualTo(10_000);
        _ = await Assert.That(duration.GetProperty("maximum").GetInt64()).IsEqualTo(4_294_967_294);
        _ = await Assert.That(schema.RootElement.GetProperty("additionalProperties").GetBoolean()).IsFalse();

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
            _ = await Assert.That(await tool.Execute(arguments, cancellationToken)).StartsWith("error:");
        }

        var waiting = tool.Execute("{}", cancellationToken);
        await time.WaitForTimer(cancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        _ = await Assert.That(await waiting).IsEqualTo("Wait timed out after 10000 ms.");
    }

    [Test]
    public async Task Pending_input_wakes_a_new_wait(CancellationToken cancellationToken)
    {
        var repository = new EventRepository(_database);
        var session = Session(new UnusedProvider(), owner: null, [], repository);
        var tool = new WaitTool(session, TimeProvider.System);
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

        _ = await Assert.That(await tool.Execute("{}", cancellationToken)).IsEqualTo("Incoming activity is available.");
    }

    [Test]
    public async Task Cancellation_clears_the_wait_registration(CancellationToken cancellationToken)
    {
        var session = Session(new UnusedProvider(), owner: null, [], selectedRepository: null);
        var tool = new WaitTool(session, TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        var waiting = tool.Execute("{}", canceled.Token);
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
        var factory = new WaitToolFactory(TimeProvider.System);
        var session = Session(provider, owner: null, [factory], repository);

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
            .IsEqualTo("Incoming activity is available.");
        _ = await Assert.That(session.State).IsEqualTo(DrainState.Running);

        provider.Release();
        await session.Settled();
        _ = await Assert.That(session.State).IsEqualTo(DrainState.Idle);
        var lifecycle = repository.Replay().Where(published => published.PayloadCase is
            Event.PayloadOneofCase.ToolStarted or Event.PayloadOneofCase.ToolFinished).ToArray();
        _ = await Assert.That(lifecycle.Length).IsEqualTo(2);
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
        await using var owner = new AgentUserSession(
            "user",
            "main",
            router.Resolve(model.Selector),
            "build",
            Resources("user"),
            sessions,
            new UserSessionModes(new ModeRegistry(TestModels.ProfileRegistry()), Path.Combine(_root, "plans")));

        _ = await owner.Send("first", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = owner.Queues.Create("ignored", string.Empty);
        _ = owner.Queues.Push("ignored", ["no wake"], Parrot.Queues.QueueDirection.Back);
        _ = owner.Queues.Create("work", string.Empty);
        _ = owner.Queues.Monitor("work", true);
        _ = owner.Queues.Push("work", ["queued"], Parrot.Queues.QueueDirection.Back);
        await owner.NotifyQueuePush(cancellationToken);
        _ = await Assert.That(owner.Queues.Get("work").Size).IsEqualTo(1);

        provider.Release();
        _ = await sessions.WaitForSession(cancellationToken);
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(string.Join('\n', provider.Requests[1].Messages.Select(message => message.Content)))
            .Contains("Queue notification from \"work\":\n\nqueued");
        _ = await Assert.That(owner.Queues.Get("work").Size).IsEqualTo(0);
        _ = await Assert.That(owner.Queues.Get("ignored").Size).IsEqualTo(1);
        provider.Release();
    }

    private static async Task WaitUntil(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private SessionResourceLease Resources(string ownerId) => SessionResourceLease.Own(
        new UserSessionResources(
            new StatePaths(_root, Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse(ownerId),
            ProjectWorkspace.FromLaunchDirectory(_root)),
        _database);

    private AgentSession Session(
        ILLMProvider provider,
        AgentUserSession? owner,
        IReadOnlyList<IToolFactory> tools,
        EventRepository? selectedRepository)
    {
        var repository = selectedRepository ?? new EventRepository(_database);
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        return new AgentSession(
            AgentIdentity.Main("agent", "main"),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            _broker,
            repository,
            tools,
            TestModels.PromptProvider(_root, _root),
            new TodoCollection("agent", repository, _broker),
            new ToolOutputBlobStore(Path.Combine(_root, "blobs")),
            new Compactor(120_000),
            null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            null,
            null,
            owner,
            CancellationToken.None);
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

    private sealed class WaitAgentSessions(ModelRouter router, TimeProvider timeProvider, string root) : IAgentSessionFactorySource
    {
        private readonly TaskCompletionSource<AgentSession> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAgentSessionFactory Create(AgentUserSession owner) => new Factory(this, owner, router, timeProvider, root);

        public Parrot.Process.ShellProcessOwners CreateShellProcesses(AgentUserSession owner) =>
            new(owner.Resources, new Parrot.Process.ProcessRunner(string.Empty), owner.Lifetime);

        public Parrot.Queues.QueueStore CreateQueues(AgentUserSession owner) =>
            new(Path.Combine(root, "queues", owner.Id));

        public async Task<AgentSession> WaitForSession(CancellationToken cancellationToken) =>
            await _created.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        private sealed class Factory(
            WaitAgentSessions source,
            AgentUserSession owner,
            ModelRouter router,
            TimeProvider timeProvider,
            string root) : IAgentSessionFactory
        {
            public IAgentSessionLease Create(
                AgentIdentity identity,
                ModelSelector model,
                EventBroker eventBroker,
                EventRepository eventRepository,
                MainAgentProfile? profile,
                SecurityProfile securityProfile,
                Parrot.Statuses.RuntimeStatus? status,
                AgentRegistry registry,
                CancellationToken lifetime)
            {
                var processes = owner.ShellProcesses.Prepare(identity.SessionId);
                owner.ShellProcesses.Register(processes);
                var session = new AgentSession(
                    identity,
                    model,
                    router,
                    eventBroker,
                    eventRepository,
                    [new WaitToolFactory(timeProvider)],
                    TestModels.PromptProvider(root, root),
                    new TodoCollection(identity.SessionId, eventRepository, eventBroker),
                    new ToolOutputBlobStore(Path.Combine(root, "blobs")),
                    new Compactor(120_000),
                    profile,
                    securityProfile,
                    status,
                    registry,
                    owner,
                    lifetime);
                _ = source._created.TrySetResult(session);
                return new AgentSessionLease(session);
            }
        }
    }
}

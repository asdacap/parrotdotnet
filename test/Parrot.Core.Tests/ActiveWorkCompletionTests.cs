using System.Runtime.Versioning;
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

namespace Parrot.Core.Tests;

internal sealed class ActiveWorkCompletionTests : IAsyncDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-active-work-completion-tests", Guid.NewGuid().ToString("n"));

    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();
    private readonly List<IAgentSessionScope> _rootScopes = [];

    public ActiveWorkCompletionTests() => Directory.CreateDirectory(_workspace);

    public async ValueTask DisposeAsync()
    {
        foreach (var rootScope in _rootScopes)
        {
            TestModels.UnregisterScope(rootScope);
            await rootScope.DisposeAsync().ConfigureAwait(false);
        }

        _broker.Dispose();
        _database.Dispose();

        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task Enabled_profile_defers_completion_until_direct_work_settles(
        CancellationToken cancellationToken)
    {
        using var parentProvider = new HeldProvider("parent", Answer("ignored"), Answer("finished"), Answer("finished"));
        using var childProvider = new HeldProvider("child", Answer("child finished"));
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processes = Processes(string.Empty, lifetime.Token);
        var repository = new EventRepository(_database);
        using var queueCatalog = new AgentQueueCatalog(Resources());
        var factory = new CompletionAgentSessions(
            router, processes, repository, _broker, queueCatalog, _workspace);
        await using var registry = new AgentRegistry(
            factory, _broker, repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, new RetainedAgentBudget(1024), lifetime.Token);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: true, maxTurns: 4);
        await using var parent = Session("parent", parentProvider, router, repository, registry, processes, queueCatalog, status, mode, lifetime.Token);
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, router),
            "worker",
            new ModelSelector("child/model"),
            "direct-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var subscription = _broker.Subscribe();

        _ = await child.Send("work", cancellationToken);
        await childProvider.Arrived(cancellationToken);
        _ = await parent.Send("finish", cancellationToken);
        await parentProvider.Arrived(cancellationToken);
        parentProvider.Release();
        await parentProvider.Arrived(cancellationToken);

        var beforeSettlement = Events(subscription);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnStarted)).IsEqualTo(1);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(0);
        _ = await Assert.That(mode.Completions).IsEqualTo(0);
        _ = await Assert.That(Reminders(beforeSettlement, "parent")).HasSingleItem();
        _ = await Assert.That(parentProvider.Requests[1].Messages.Any(message =>
            message.Role == LLMRole.System
            && message.Content.Contains(child.SessionId, StringComparison.Ordinal))).IsTrue();

        childProvider.Release();
        _ = await child.Wait(0, cancellationToken);
        parentProvider.Release();
        await parentProvider.Arrived(cancellationToken);
        parentProvider.Release();
        _ = await parent.Wait(0, cancellationToken);
        await parent.DisposeAsync();

        var allEvents = beforeSettlement.Concat(Events(subscription)).ToArray();
        _ = await Assert.That(parentProvider.Requests).Count().IsEqualTo(3);
        _ = await Assert.That(parentProvider.Requests[2].Messages.Any(message =>
            message.Role == LLMRole.System
            && message.Content.Contains(child.SessionId, StringComparison.Ordinal))).IsTrue();
        _ = await Assert.That(Reminders(allEvents, "parent")).HasSingleItem();
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnStarted)).IsEqualTo(2);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(2);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(2);
        _ = await Assert.That(mode.Completions).IsEqualTo(2);
        _ = await Assert.That(repository.Messages("parent").Count(message =>
            message.StartsWith("assistant:", StringComparison.Ordinal))).IsEqualTo(2);
        _ = await Assert.That(repository.Messages("parent")[^1]).IsEqualTo("assistant: finished");
    }

    [Test]
    public async Task Completion_callback_observes_exit_reminder_changed_after_session_construction(
        CancellationToken cancellationToken)
    {
        using var provider = new HeldProvider("parent", Answer("premature"), Answer("finished"));
        var router = Router(provider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processes = Processes(string.Empty, lifetime.Token);
        var repository = new EventRepository(_database);
        using var queueCatalog = new AgentQueueCatalog(Resources());
        var factory = new CompletionAgentSessions(
            router, processes, repository, _broker, queueCatalog, _workspace);
        await using var registry = new AgentRegistry(
            factory,
            _broker,
            repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1024),
            lifetime.Token);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        await using var parent = Session(
            "parent",
            provider,
            router,
            repository,
            registry,
            processes,
            queueCatalog,
            status,
            new CompletionMode(enforce: false, maxTurns: 3),
            lifetime.Token);

        var goals = TestModels.ScopeOf(parent).Goals;
        await goals.SetGoal("changed after construction", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.ExitReminderInjected))
            .IsEqualTo(1);
        _ = await Assert.That(provider.Requests[1].Messages).Contains(message =>
            message.Role == LLMRole.System
            && message.Content.Contains("changed after construction", StringComparison.Ordinal));

        goals.ClearGoal();
        provider.Release();
        await parent.DisposeAsync();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
    }

    [Test]
    public async Task Pending_child_questions_guard_completion_before_active_work_and_mode(
        CancellationToken cancellationToken)
    {
        using var parentProvider = new HeldProvider("parent", Answer("premature"), Answer("finished"));
        using var childProvider = new HeldProvider("child");
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processes = Processes(string.Empty, lifetime.Token);
        var repository = new EventRepository(_database);
        using var queueCatalog = new AgentQueueCatalog(Resources());
        var factory = new CompletionAgentSessions(
            router,
            processes,
            repository,
            _broker,
            queueCatalog,
            _workspace);
        await using var registry = new AgentRegistry(
            factory, _broker, repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, new RetainedAgentBudget(1024), lifetime.Token);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: false, maxTurns: 3);
        await using var parent = Session(
            "parent",
            parentProvider,
            router,
            repository,
            registry,
            processes,
            queueCatalog,
            status,
            mode,
            lifetime.Token);
        var ownedChildQuestions = _rootScopes[^1].ChildQuestions;
        var firstChild = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "first")).Session;
        var secondChild = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(QuestionChildRequest(parent, router, "second")).Session;
        using var subscription = _broker.Subscribe();

        _ = await parent.Send("finish", cancellationToken);
        await parentProvider.Arrived(cancellationToken);
        var firstQuestion = ownedChildQuestions.Ask(firstChild, [Question("first")], cancellationToken);
        var secondQuestion = ownedChildQuestions.Ask(secondChild, [Question("second")], cancellationToken);
        _ = await WaitForPendingQuestions(ownedChildQuestions, parent, 2, cancellationToken);
        parentProvider.Release();
        await parentProvider.Arrived(cancellationToken);

        var beforeAnswers = Events(subscription);
        _ = await Assert.That(mode.Completions).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(0);
        _ = await Assert.That(beforeAnswers.Count(published =>
            published.AgentSessionId == "parent"
            && published.PayloadCase == Event.PayloadOneofCase.PendingChildQuestionReminderInjected)).IsEqualTo(1);
        _ = await Assert.That(parentProvider.Requests[1].Messages).Contains(message =>
            message.Role == LLMRole.Assistant && message.Content == "premature");
        _ = await Assert.That(parentProvider.Requests[1].Messages).Contains(message =>
            message.Role == LLMRole.System
            && message.Content.Contains($"first ({firstChild.SessionId})", StringComparison.Ordinal)
            && message.Content.Contains($"second ({secondChild.SessionId})", StringComparison.Ordinal)
            && message.Content.Contains("answer", StringComparison.Ordinal));

        ownedChildQuestions.Reply(TestModels.ScopeOf(parent).ParentScope, firstChild.SessionId, QuestionAnswer("first"));
        ownedChildQuestions.Reply(TestModels.ScopeOf(parent).ParentScope, secondChild.SessionId, QuestionAnswer("second"));
        _ = await Task.WhenAll(firstQuestion, secondQuestion);
        parentProvider.Release();
        _ = await parent.Wait(0, cancellationToken);
        await parent.DisposeAsync();

        _ = await Assert.That(mode.Completions).IsEqualTo(1);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(1);
    }

    [Test]
    public async Task Disabled_profile_does_not_defer_for_active_direct_work(CancellationToken cancellationToken)
    {
        using var parentProvider = new HeldProvider("parent", Answer("finished"));
        using var childProvider = new HeldProvider("child", Answer("child finished"));
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processes = Processes(string.Empty, lifetime.Token);
        var repository = new EventRepository(_database);
        using var queueCatalog = new AgentQueueCatalog(Resources());
        var factory = new CompletionAgentSessions(
            router, processes, repository, _broker, queueCatalog, _workspace);
        await using var registry = new AgentRegistry(
            factory, _broker, repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, new RetainedAgentBudget(1024), lifetime.Token);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: false, maxTurns: 2);
        await using var parent = Session("parent", parentProvider, router, repository, registry, processes, queueCatalog, status, mode, lifetime.Token);
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, router),
            "worker",
            new ModelSelector("child/model"),
            "direct-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var subscription = _broker.Subscribe();

        _ = await child.Send("work", cancellationToken);
        await childProvider.Arrived(cancellationToken);
        _ = await parent.Send("finish", cancellationToken);
        await parentProvider.Arrived(cancellationToken);
        parentProvider.Release();
        _ = await parent.Wait(0, cancellationToken);
        await parent.DisposeAsync();

        _ = await Assert.That(parentProvider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(Reminders(Events(subscription), "parent")).IsEmpty();
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(1);
        _ = await Assert.That(mode.Completions).IsEqualTo(1);
    }

    [Test]
    public async Task Enforcement_excludes_active_siblings_and_grandchildren(CancellationToken cancellationToken)
    {
        using var monitoredProvider = new HeldProvider("monitored", Answer("finished"));
        using var siblingProvider = new HeldProvider("sibling", Answer("sibling finished"));
        using var grandchildProvider = new HeldProvider("grandchild", Answer("grandchild finished"));
        using var rootProvider = new HeldProvider("root");
        var router = Router(monitoredProvider, siblingProvider, grandchildProvider, rootProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processes = Processes(string.Empty, lifetime.Token);
        var repository = new EventRepository(_database);
        using var queueCatalog = new AgentQueueCatalog(Resources());
        var factory = new CompletionAgentSessions(
            router, processes, repository, _broker, queueCatalog, _workspace);
        await using var registry = new AgentRegistry(
            factory, _broker, repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, new RetainedAgentBudget(1024), lifetime.Token);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        await using var root = Session(
            "root",
            rootProvider,
            router,
            repository,
            registry,
            processes,
            queueCatalog,
            status,
            new CompletionMode(enforce: true, maxTurns: 2),
            lifetime.Token);
        var monitored = TestModels.ScopeOf(root).ChildRegistry.SpawnScope(new AgentLaunchRequest(
            root,
            Turn(root, router),
            "worker",
            new ModelSelector("monitored/model"),
            "monitored",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        monitored.UpdateSelection(
            new ModelSelector("monitored/model"),
            new CompletionMode(enforce: true, maxTurns: 2));
        var sibling = TestModels.ScopeOf(root).ChildRegistry.SpawnScope(new AgentLaunchRequest(
            root,
            Turn(root, router),
            "worker",
            new ModelSelector("sibling/model"),
            "sibling",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var grandchild = TestModels.ScopeOf(sibling).ChildRegistry.SpawnScope(new AgentLaunchRequest(
            sibling,
            Turn(sibling, router),
            "worker",
            new ModelSelector("grandchild/model"),
            "grandchild",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var subscription = _broker.Subscribe();

        _ = await sibling.Send("work", cancellationToken);
        await siblingProvider.Arrived(cancellationToken);
        _ = await grandchild.Send("work", cancellationToken);
        await grandchildProvider.Arrived(cancellationToken);
        _ = await monitored.Send("finish", cancellationToken);
        await monitoredProvider.Arrived(cancellationToken);
        monitoredProvider.Release();
        await monitored.DisposeAsync();

        _ = await Assert.That(monitoredProvider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(Reminders(Events(subscription), monitored.SessionId)).IsEmpty();
        _ = await Assert.That(Payloads(
            repository, monitored.SessionId, Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
    }

    [Test]
    public async Task Enforcement_uses_only_the_sessions_local_shell_process_owner(
        CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsLinux())
        {
            return;
        }

        using var provider = new HeldProvider("parent", Answer("finished"));
        var router = Router(provider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processes = Processes(CreateSandboxPassThrough(), lifetime.Token);
        var repository = new EventRepository(_database);
        using var queueCatalog = new AgentQueueCatalog(Resources());
        var factory = new CompletionAgentSessions(
            router, processes, repository, _broker, queueCatalog, _workspace);
        await using var registry = new AgentRegistry(
            factory, _broker, repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, new RetainedAgentBudget(1024), lifetime.Token);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        await using var parent = Session(
            "parent",
            provider,
            router,
            repository,
            registry,
            processes,
            queueCatalog,
            status,
            new CompletionMode(enforce: true, maxTurns: 2),
            lifetime.Token);
        var otherProvider = new HeldProvider("other");
        var otherRouter = Router(otherProvider);
        var otherProcesses = processes.Prepare("other");
        processes.Register(otherProcesses);
        await using var other = BareSession(
            "other",
            otherProvider,
            otherRouter,
            repository,
            registry,
            queueCatalog,
            status,
            otherProcesses,
            lifetime.Token);
        var process = otherProcesses.Start(
            "other-work",
            "sleep 30",
            "call",
            ProcessEnvironmentOverrides.Empty,
            other,
            SecurityProfile.Compose(readOnly: false, [], [], []));
        _ = await process.Wait(TimeSpan.Zero, cancellationToken);
        using var subscription = _broker.Subscribe();

        _ = await parent.Send("finish", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await parent.DisposeAsync();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(Reminders(Events(subscription), "parent")).IsEmpty();
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);

        await lifetime.CancelAsync();
        await processes.Settle();
        otherProvider.Dispose();
    }

    [Test]
    public async Task Repeated_ignored_reminders_reach_the_turn_limit_without_completing_plan(
        CancellationToken cancellationToken)
    {
        using var parentProvider = new HeldProvider("parent", Answer("one"), Answer("two"), Answer("three"));
        using var childProvider = new HeldProvider("child", Answer("child finished"));
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processes = Processes(string.Empty, lifetime.Token);
        var repository = new EventRepository(_database);
        using var queueCatalog = new AgentQueueCatalog(Resources());
        var factory = new CompletionAgentSessions(
            router, processes, repository, _broker, queueCatalog, _workspace);
        await using var registry = new AgentRegistry(
            factory, _broker, repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, new RetainedAgentBudget(1024), lifetime.Token);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: true, maxTurns: 3);
        await using var parent = Session("parent", parentProvider, router, repository, registry, processes, queueCatalog, status, mode, lifetime.Token);
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, router),
            "worker",
            new ModelSelector("child/model"),
            "direct-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var subscription = _broker.Subscribe();

        _ = await child.Send("work", cancellationToken);
        await childProvider.Arrived(cancellationToken);
        _ = await parent.Send("finish", cancellationToken);
        for (var request = 0; request < 3; request++)
        {
            await parentProvider.Arrived(cancellationToken);
            parentProvider.Release();
        }

        _ = await parent.Wait(0, cancellationToken);
        await parent.DisposeAsync();

        var events = Events(subscription);
        _ = await Assert.That(parentProvider.Requests).Count().IsEqualTo(3);
        _ = await Assert.That(Reminders(events, "parent")).Count().IsEqualTo(3);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnStarted)).IsEqualTo(1);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(0);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnFailed)).IsEqualTo(1);
        _ = await Assert.That(mode.Completions).IsEqualTo(0);
        _ = await Assert.That(repository.Replay().Single(published =>
            published.AgentSessionId == "parent"
            && published.PayloadCase == Event.PayloadOneofCase.TurnFailed).TurnFailed.Message)
            .IsEqualTo("the turn exceeded its provider-request limit");
    }

    [Test]
    public async Task Interruption_bypasses_active_work_completion_enforcement(CancellationToken cancellationToken)
    {
        using var parentProvider = new HeldProvider("parent", Answer("unreachable"));
        using var childProvider = new HeldProvider("child", Answer("child finished"));
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var processes = Processes(string.Empty, lifetime.Token);
        var repository = new EventRepository(_database);
        using var queueCatalog = new AgentQueueCatalog(Resources());
        var factory = new CompletionAgentSessions(
            router, processes, repository, _broker, queueCatalog, _workspace);
        await using var registry = new AgentRegistry(
            factory, _broker, repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, new RetainedAgentBudget(1024), lifetime.Token);
        var status = new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System);
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: true, maxTurns: 2);
        await using var parent = Session("parent", parentProvider, router, repository, registry, processes, queueCatalog, status, mode, lifetime.Token);
        var child = TestModels.ScopeOf(parent).ChildRegistry.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, router),
            "worker",
            new ModelSelector("child/model"),
            "direct-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var subscription = _broker.Subscribe();

        _ = await child.Send("work", cancellationToken);
        await childProvider.Arrived(cancellationToken);
        _ = await parent.Send("finish", cancellationToken);
        await parentProvider.Arrived(cancellationToken);
        await parent.Interrupt(cancellationToken);

        _ = await Assert.That(Reminders(Events(subscription), "parent")).IsEmpty();
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(0);
        _ = await Assert.That(mode.Completions).IsEqualTo(0);
        _ = await Assert.That(repository.Replay().Single(published =>
            published.AgentSessionId == "parent"
            && published.PayloadCase == Event.PayloadOneofCase.TurnEnded).TurnEnded.FinishReason)
            .IsEqualTo("interrupted");
    }

    private static LLMEvent Answer(string text) => LLMEvent.Completed("stop", 1, 0, 1, text, []);

    private static AgentLaunchRequest QuestionChildRequest(
        IAgentSession parent,
        ModelRouter router,
        string name) => new(
            parent,
            Turn(parent, router),
            "worker",
            new ModelSelector("child/model"),
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);

    private static Parrot.Questions.QuestionDefinition Question(string id) => new(
        id,
        "Continue?",
        ["Yes"],
        false,
        false);

    private static QuestionReply QuestionAnswer(string id) =>
        new([new Parrot.Questions.QuestionAnswer(id)]);

    private static async Task<IReadOnlyList<PendingChildQuestionRequest>> WaitForPendingQuestions(
        ChildQuestionCoordinator coordinator,
        IAgentSession parent,
        int count,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = coordinator.Pending(parent);
            if (pending.Count == count)
            {
                return pending;
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static AgentTurnSelection Turn(IAgentSession session, ModelRouter router)
    {
        var selection = session.Selection();
        return new AgentTurnSelection(
            selection.RequestedModel,
            router.Resolve(selection.RequestedModel.Value),
            selection.Profile,
            selection.SecurityProfile);
    }

    private static List<Event> Events(EventSubscription subscription)
    {
        var events = new List<Event>();
        while (subscription.Reader.TryRead(out var published))
        {
            events.Add(published);
        }

        return events;
    }

    private static IReadOnlyList<Event> Reminders(IEnumerable<Event> events, string sessionId) =>
        [.. events.Where(published => published.AgentSessionId == sessionId
            && published.PayloadCase == Event.PayloadOneofCase.ActiveWorkReminderInjected)];

    private static int Payloads(
        EventRepository repository,
        string sessionId,
        Event.PayloadOneofCase payload) =>
        repository.Replay().Count(published =>
            published.AgentSessionId == sessionId && published.PayloadCase == payload);

    private static ModelRouter Router(params HeldProvider[] providers)
    {
        var models = providers.ToDictionary<ILLMProvider, string, IReadOnlyList<LLMModel>>(
            provider => provider.Id,
            provider => [new LLMModel("model", provider.Id)],
            StringComparer.Ordinal);
        var registry = new ProviderRegistry(providers, models);
        return new ModelRouter(
            registry,
            new ModelAliasCatalog(registry, []),
            $"{providers[0].Id}/model");
    }

    private IAgentSession Session(
        string sessionId,
        HeldProvider provider,
        ModelRouter router,
        EventRepository repository,
        AgentRegistry registry,
        ShellProcessOwners processes,
        AgentQueueCatalog queueCatalog,
        RuntimeStatus status,
        IMode mode,
        CancellationToken lifetime)
    {
        var owner = processes.Prepare(sessionId);
        processes.Register(owner);
        var identity = AgentIdentity.Main(sessionId, sessionId, TestModels.PromptTemplates);
        var queues = queueCatalog.Register(identity);
        var rootScope = AgentSessionDirectScope.Build(identity, AgentSessionParentLink.Root(), registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) =>
        {
            var exitReminder = new ExitReminder(repository, TestModels.PromptTemplates, identity.SessionId);
            return new AgentSession(identity, sessionParentScope, new ModelSelector($"{provider.Id}/model"), router, _broker, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, _workspace, _workspace), new ToolOutputBlobStore(_workspace), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, mode, TestModels.CompletionCallbacks(childQuestions, new ActiveWorkCompletionReminder(children, owner, TestModels.PromptTemplates), exitReminder, repository, _broker), SecurityProfileTestFactory.Create(mode.SecurityProfile), status, queues, new AgentSessionActivity(TimeProvider.System), lifetime);
        });
        TestModels.RegisterScope(rootScope);
        queues.Attach(rootScope.Session);
        registry.RegisterRootScope(rootScope);
        _rootScopes.Add(rootScope);
        return rootScope.Session;
    }

    private AgentSession BareSession(
        string sessionId,
        HeldProvider provider,
        ModelRouter router,
        EventRepository repository,
        AgentRegistry registry,
        AgentQueueCatalog queueCatalog,
        RuntimeStatus status,
        ShellProcessOwner processes,
        CancellationToken lifetime)
    {
        var identity = AgentIdentity.Main(sessionId, sessionId, TestModels.PromptTemplates);
        var queues = queueCatalog.Register(identity);
        var mode = TestModels.Profile();
        var children = new ChildRegistry(
            identity,
            registry,
            () => throw new InvalidOperationException("The test session has no owner scope."));
        var childQuestions = new ChildQuestionCoordinator(AgentSessionParentScope.Root(), TestModels.PromptTemplates);
        var exitReminder = new ExitReminder(repository, TestModels.PromptTemplates, identity.SessionId);
        var session = new AgentSession(identity, AgentSessionParentScope.Root(), new ModelSelector($"{provider.Id}/model"), router, _broker, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, _workspace, _workspace), new ToolOutputBlobStore(_workspace), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, mode, TestModels.CompletionCallbacks(childQuestions, new ActiveWorkCompletionReminder(children, processes, TestModels.PromptTemplates), exitReminder, repository, _broker), SecurityProfileTestFactory.Create(mode.SecurityProfile), status, queues, new AgentSessionActivity(TimeProvider.System), lifetime);
        queues.Attach(session);
        return session;
    }

    private ShellProcessOwners Processes(string sandbox, CancellationToken lifetime) =>
        new(Resources(), new ProcessRunner(sandbox), lifetime);

    private UserSessionResources Resources() =>
        new(
            new StatePaths(
                Path.Combine(_workspace, ".state"),
                Path.Combine(_workspace, ".config"),
                Path.Combine(_workspace, ".data")),
            UserSessionId.Parse($"session-{Guid.NewGuid():n}"),
            ProjectWorkspace.FromLaunchDirectory(_workspace));

    [SupportedOSPlatform("linux")]
    private string CreateSandboxPassThrough()
    {
        var path = Path.Combine(_workspace, $"sandbox-{Guid.NewGuid():n}");
        var script = "#!/bin/sh\nwhile [ \"$1\" != \"--\" ]; do\n"
            + "  if [ \"$1\" = \"--chdir\" ]; then shift; cd \"$1\" || exit; "
            + "elif [ \"$1\" = \"--setenv\" ]; then export \"$2=$3\"; shift 2; fi\n"
            + "  shift\ndone\nshift\nexec \"$@\"\n";
        File.WriteAllText(path, script);
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }

    private sealed class CompletionMode(bool enforce, int maxTurns) : IMode
    {
        public int Completions { get; private set; }

        public string Id => "test";

        public string Prompt => "Test prompt.";

        public IReadOnlyList<string>? AllowedTools => null;

        public IReadOnlyList<string> DisabledTools => [];

        public int MaxTurns => maxTurns;

        public bool EnforceActiveWorkCompletion => enforce;

        public bool IsUserSelectable => false;

        public bool IsAgentSelectable => false;

        public SecurityProfile SecurityProfile { get; } = SecurityProfile.Compose(readOnly: false, [], [], []);

        public void Prepare()
        {
        }

        public ModeCompletionOutcome Complete(string sessionId, string messageId)
        {
            Completions++;
            var completion = new PlanCompleted
            {
                AgentSessionId = sessionId,
                MessageId = messageId,
                Markdown = "# Completed",
            };

            return ModeCompletionOutcome.Completed(completion);
        }
    }

    private sealed class CompletionAgentSessions(
        ModelRouter router,
        ShellProcessOwners processes,
        EventRepository repository,
        EventBroker broker,
        AgentQueueCatalog queueCatalog,
        string workspace) : IAgentSessionFactory
    {
        public IAgentSessionScope Create(
            AgentIdentity identity,
            AgentSessionParentLink parentLink,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IMode mode,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            IAgentRegistry registry,
            CancellationToken lifetime)
        {
            var owner = processes.Prepare(identity.SessionId);
            processes.Register(owner);
            var queues = queueCatalog.Register(identity);
            var scope = AgentSessionDirectScope.Build(identity, parentLink, registry, TestModels.PromptTemplates, (sessionParentScope, _, children, scopedChildQuestions) =>
            {
                var exitReminder = new ExitReminder(repository, TestModels.PromptTemplates, identity.SessionId);
                var session = new AgentSession(
                identity,
                sessionParentScope,
                model,
                router,
                broker,
                repository,
                [],
                TestModels.EmptyToolDefinitions,
                TestModels.MaterializePrompt(identity, workspace, workspace),
                new ToolOutputBlobStore(workspace),
                TestModels.CompactionGroupBlobs(),
                new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates),
                new ContextCadence(),
                TestModels.PromptTemplates,
                scopedChildQuestions,
                exitReminder,
                mode,
                TestModels.CompletionCallbacks(
                    scopedChildQuestions,
                    new ActiveWorkCompletionReminder(children, owner, TestModels.PromptTemplates),
                    exitReminder,
                    eventRepository,
                    eventBroker),
                SecurityProfileTestFactory.Create(securityProfile),
                status,
                queues,
                new AgentSessionActivity(TimeProvider.System),
                lifetime);
                queues.Attach(session);
                return session;
            });
            TestModels.RegisterScope(scope);
            return scope;
        }
    }

    private sealed class HeldProvider(string id, params LLMEvent[] answers) : ILLMProvider, IDisposable
    {
        private readonly Queue<LLMEvent> _answers = new(answers);
        private readonly List<LLMRequest> _requests = [];
        private readonly Lock _gate = new();
        private readonly SemaphoreSlim _arrived = new(0);
        private readonly SemaphoreSlim _released = new(0);

        public string Id { get; } = id;

        public IReadOnlyList<LLMRequest> Requests
        {
            get
            {
                lock (_gate)
                {
                    return [.. _requests];
                }
            }
        }

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LLMModel>>([]);

        public async IAsyncEnumerable<LLMEvent> Call(
            LLMRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            LLMEvent answer;
            lock (_gate)
            {
                _requests.Add(request);
                answer = _answers.Count == 0
                    ? Answer("nothing scripted")
                    : _answers.Dequeue();
            }

            _ = _arrived.Release();
            await _released.WaitAsync(cancellationToken).ConfigureAwait(false);
            yield return answer;
        }

        public Task Arrived(CancellationToken cancellationToken) => _arrived.WaitAsync(cancellationToken);

        public void Release() => _released.Release();

        public void Dispose()
        {
            _arrived.Dispose();
            _released.Dispose();
        }
    }
}

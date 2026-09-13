using System.Runtime.Versioning;
using Parrot.Agent;
using Parrot.Config;
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
    private readonly IEventBroker _broker = new EventBroker();
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
        using var parentProvider = new HeldProvider("parent", LLMEvent.Completed("stop", 1, 0, 1, "ignored", []), LLMEvent.Completed("stop", 1, 0, 1, "finished", []), LLMEvent.Completed("stop", 1, 0, 1, "finished", []));
        using var childProvider = new HeldProvider("child", LLMEvent.Completed("stop", 1, 0, 1, "child finished", []));
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = new UserSessionResources(new StatePaths(Path.Combine(_workspace, ".state"), Path.Combine(_workspace, ".config"), Path.Combine(_workspace, ".data")), UserSessionId.Parse($"session-{Guid.NewGuid():n}"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(string.Empty);
        var repository = new EventRepository(_database);
        var factory = new CompletionAgentSessions(
            router, runner, resources, _broker, _workspace);
        await using IAgentRegistry registry = new AgentRegistry(
            factory, _broker, repository, new TestProfileFixture().Registry, TestModels.PromptTemplates, new RetainedAgentBudget(1024), TestDiagnosticLog.Instance, lifetime.Token);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: true, maxTurns: 4);
        await using var parent = Session("parent", parentProvider, router, repository, registry, runner, resources, status, mode, lifetime.Token);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            new ModelSelector("child/model"),
            "direct-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var subscription = _broker.Subscribe();

        _ = await child.SendTextMessage("work", cancellationToken);
        await childProvider.Arrived(cancellationToken);
        _ = await parent.SendTextMessage("finish", cancellationToken);
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
    [Arguments(false, true, true)]
    [Arguments(true, true, true)]
    [Arguments(false, false, true)]
    [Arguments(true, false, true)]
    [Arguments(false, true, false)]
    public async Task Completion_requires_owned_queues_to_be_drained(
        bool closed,
        bool populated,
        bool enforce,
        CancellationToken cancellationToken)
    {
        using var provider = new HeldProvider("parent", LLMEvent.Completed("stop", 1, 0, 1, "stopping", []), LLMEvent.Completed("stop", 1, 0, 1, "finished", []));
        var router = Router(provider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = new UserSessionResources(new StatePaths(Path.Combine(_workspace, ".state"), Path.Combine(_workspace, ".config"), Path.Combine(_workspace, ".data")), UserSessionId.Parse($"session-{Guid.NewGuid():n}"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(string.Empty);
        var repository = new EventRepository(_database);
        var factory = new CompletionAgentSessions(router, runner, resources, _broker, _workspace);
        await using IAgentRegistry registry = new AgentRegistry(
            factory, _broker, repository, new TestProfileFixture().Registry, TestModels.PromptTemplates, new RetainedAgentBudget(1024), TestDiagnosticLog.Instance, lifetime.Token);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce, maxTurns: 3);
        await using var parent = Session("parent", provider, router, repository, registry, runner, resources, status, mode, lifetime.Token);
        var queues = TestModels.ScopeOf(parent).GetService<IAgentQueues>();
        _ = queues.Create("owned-work", string.Empty);
        _ = await queues.Push("owned-work", populated ? ["pending"] : [], QueueDirection.Back, closed, cancellationToken);
        using var subscription = _broker.Subscribe();

        _ = await parent.SendTextMessage("Stop now", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        var blocked = populated && enforce;
        if (blocked)
        {
            await provider.Arrived(cancellationToken);
            _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(0);
            _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(0);
            _ = await Assert.That(mode.Completions).IsEqualTo(0);
            _ = await Assert.That(provider.Requests[1].Messages).Contains(message =>
                message.Role == LLMRole.System
                && message.Content.Contains("owned-work (remaining items: 1)", StringComparison.Ordinal)
                && message.Content.Contains("even when asked to stop", StringComparison.Ordinal));
            _ = queues.TryTake("owned-work", 1, QueueDirection.Front);
            provider.Release();
        }

        _ = await parent.Wait(0, cancellationToken);
        await parent.DisposeAsync();
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(blocked ? 2 : 1);
        _ = await Assert.That(Reminders(Events(subscription), "parent")).Count().IsEqualTo(blocked ? 1 : 0);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.PlanCompleted)).IsEqualTo(1);
        _ = await Assert.That(mode.Completions).IsEqualTo(1);
    }

    [Test]
    public async Task Completion_callback_observes_exit_reminder_changed_after_session_construction(
        CancellationToken cancellationToken)
    {
        using var provider = new HeldProvider("parent", LLMEvent.Completed("stop", 1, 0, 1, "premature", []), LLMEvent.Completed("stop", 1, 0, 1, "finished", []));
        var router = Router(provider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = new UserSessionResources(new StatePaths(Path.Combine(_workspace, ".state"), Path.Combine(_workspace, ".config"), Path.Combine(_workspace, ".data")), UserSessionId.Parse($"session-{Guid.NewGuid():n}"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(string.Empty);
        var repository = new EventRepository(_database);
        var factory = new CompletionAgentSessions(
            router, runner, resources, _broker, _workspace);
        await using IAgentRegistry registry = new AgentRegistry(
            factory,
            _broker,
            repository,
            new TestProfileFixture().Registry,
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1024),
            TestDiagnosticLog.Instance,
            lifetime.Token);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        await using var parent = Session(
            "parent",
            provider,
            router,
            repository,
            registry,
            runner,
            resources,
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
        using var parentProvider = new HeldProvider("parent", LLMEvent.Completed("stop", 1, 0, 1, "premature", []), LLMEvent.Completed("stop", 1, 0, 1, "finished", []));
        using var childProvider = new HeldProvider("child");
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = new UserSessionResources(new StatePaths(Path.Combine(_workspace, ".state"), Path.Combine(_workspace, ".config"), Path.Combine(_workspace, ".data")), UserSessionId.Parse($"session-{Guid.NewGuid():n}"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(string.Empty);
        var repository = new EventRepository(_database);
        var factory = new CompletionAgentSessions(
            router,
            runner,
            resources,
            _broker,
            _workspace);
        await using IAgentRegistry registry = new AgentRegistry(
            factory, _broker, repository, new TestProfileFixture().Registry, TestModels.PromptTemplates, new RetainedAgentBudget(1024), TestDiagnosticLog.Instance, lifetime.Token);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: false, maxTurns: 3);
        await using var parent = Session(
            "parent",
            parentProvider,
            router,
            repository,
            registry,
            runner,
            resources,
            status,
            mode,
            lifetime.Token);
        var ownedChildQuestions = _rootScopes[^1].ChildQuestions;
        var firstChild = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            new ModelSelector("child/model"),
            "first",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var secondChild = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            new ModelSelector("child/model"),
            "second",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        using var subscription = _broker.Subscribe();

        _ = await parent.SendTextMessage("finish", cancellationToken);
        await parentProvider.Arrived(cancellationToken);
        var firstQuestion = ownedChildQuestions.Ask(firstChild, [new Parrot.Questions.QuestionDefinition("first", "Continue?", ["Yes"], false, false)], cancellationToken);
        var secondQuestion = ownedChildQuestions.Ask(secondChild, [new Parrot.Questions.QuestionDefinition("second", "Continue?", ["Yes"], false, false)], cancellationToken);
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

        ownedChildQuestions.ReplyFromParent(TestModels.ScopeOf(parent).ParentScope, firstChild.SessionId, new QuestionReply([new Parrot.Questions.QuestionAnswer("first")]));
        ownedChildQuestions.ReplyFromParent(TestModels.ScopeOf(parent).ParentScope, secondChild.SessionId, new QuestionReply([new Parrot.Questions.QuestionAnswer("second")]));
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
        using var parentProvider = new HeldProvider("parent", LLMEvent.Completed("stop", 1, 0, 1, "finished", []));
        using var childProvider = new HeldProvider("child", LLMEvent.Completed("stop", 1, 0, 1, "child finished", []));
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = new UserSessionResources(new StatePaths(Path.Combine(_workspace, ".state"), Path.Combine(_workspace, ".config"), Path.Combine(_workspace, ".data")), UserSessionId.Parse($"session-{Guid.NewGuid():n}"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(string.Empty);
        var repository = new EventRepository(_database);
        var factory = new CompletionAgentSessions(
            router, runner, resources, _broker, _workspace);
        await using IAgentRegistry registry = new AgentRegistry(
            factory, _broker, repository, new TestProfileFixture().Registry, TestModels.PromptTemplates, new RetainedAgentBudget(1024), TestDiagnosticLog.Instance, lifetime.Token);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: false, maxTurns: 2);
        await using var parent = Session("parent", parentProvider, router, repository, registry, runner, resources, status, mode, lifetime.Token);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            new ModelSelector("child/model"),
            "direct-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var subscription = _broker.Subscribe();

        _ = await child.SendTextMessage("work", cancellationToken);
        await childProvider.Arrived(cancellationToken);
        _ = await parent.SendTextMessage("finish", cancellationToken);
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
    public async Task Enforcement_excludes_active_siblings_grandchildren_and_parent_queues(CancellationToken cancellationToken)
    {
        using var monitoredProvider = new HeldProvider("monitored", LLMEvent.Completed("stop", 1, 0, 1, "finished", []));
        using var siblingProvider = new HeldProvider("sibling", LLMEvent.Completed("stop", 1, 0, 1, "sibling finished", []));
        using var grandchildProvider = new HeldProvider("grandchild", LLMEvent.Completed("stop", 1, 0, 1, "grandchild finished", []));
        using var rootProvider = new HeldProvider("root");
        var router = Router(monitoredProvider, siblingProvider, grandchildProvider, rootProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = new UserSessionResources(new StatePaths(Path.Combine(_workspace, ".state"), Path.Combine(_workspace, ".config"), Path.Combine(_workspace, ".data")), UserSessionId.Parse($"session-{Guid.NewGuid():n}"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(string.Empty);
        var repository = new EventRepository(_database);
        var factory = new CompletionAgentSessions(
            router, runner, resources, _broker, _workspace);
        await using IAgentRegistry registry = new AgentRegistry(
            factory, _broker, repository, new TestProfileFixture().Registry, TestModels.PromptTemplates, new RetainedAgentBudget(1024), TestDiagnosticLog.Instance, lifetime.Token);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        await using var root = Session(
            "root",
            rootProvider,
            router,
            repository,
            registry,
            runner,
            resources,
            status,
            new CompletionMode(enforce: true, maxTurns: 2),
            lifetime.Token);
        var monitored = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            new TurnFixture(root, router).Selection,
            "worker",
            new ModelSelector("monitored/model"),
            "monitored",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var rootQueues = TestModels.ScopeOf(root).GetService<IAgentQueues>();
        _ = rootQueues.Create("parent-open", string.Empty);
        _ = rootQueues.Create("parent-closed", string.Empty);
        _ = await rootQueues.Push("parent-open", ["pending"], QueueDirection.Back, false, cancellationToken);
        _ = await rootQueues.Push("parent-closed", ["pending"], QueueDirection.Back, true, cancellationToken);
        monitored.UpdateSelection(
            new ModelSelector("monitored/model"),
            new CompletionMode(enforce: true, maxTurns: 2));
        var sibling = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            new TurnFixture(root, router).Selection,
            "worker",
            new ModelSelector("sibling/model"),
            "sibling",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var grandchild = TestModels.ScopeOf(sibling).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            sibling,
            new TurnFixture(sibling, router).Selection,
            "worker",
            new ModelSelector("grandchild/model"),
            "grandchild",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        using var subscription = _broker.Subscribe();

        _ = await sibling.SendTextMessage("work", cancellationToken);
        await siblingProvider.Arrived(cancellationToken);
        _ = await grandchild.SendTextMessage("work", cancellationToken);
        await grandchildProvider.Arrived(cancellationToken);
        _ = await monitored.SendTextMessage("finish", cancellationToken);
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

        using var provider = new HeldProvider("parent", LLMEvent.Completed("stop", 1, 0, 1, "finished", []));
        var router = Router(provider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = new UserSessionResources(new StatePaths(Path.Combine(_workspace, ".state"), Path.Combine(_workspace, ".config"), Path.Combine(_workspace, ".data")), UserSessionId.Parse($"session-{Guid.NewGuid():n}"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(CreateSandboxPassThrough());
        var repository = new EventRepository(_database);
        var factory = new CompletionAgentSessions(
            router, runner, resources, _broker, _workspace);
        await using IAgentRegistry registry = new AgentRegistry(
            factory, _broker, repository, new TestProfileFixture().Registry, TestModels.PromptTemplates, new RetainedAgentBudget(1024), TestDiagnosticLog.Instance, lifetime.Token);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        await using var parent = Session(
            "parent",
            provider,
            router,
            repository,
            registry,
            runner,
            resources,
            status,
            new CompletionMode(enforce: true, maxTurns: 2),
            lifetime.Token);
        var otherProvider = new HeldProvider("other");
        var otherRouter = Router(otherProvider);
        await using var other = Session(
            "other",
            otherProvider,
            otherRouter,
            repository,
            registry,
            runner,
            resources,
            status,
            new TestProfileFixture().Mode,
            lifetime.Token);
        var otherProcesses = TestModels.ScopeOf(other).Processes;
        var process = otherProcesses.StartPipe(
            "other-work",
            "sleep 30",
            "call",
            ProcessEnvironmentOverrides.Empty,
            other,
            SecurityProfile.Compose(readOnly: false, [], [], []));
        _ = await process.Wait(TimeSpan.Zero, cancellationToken);
        using var subscription = _broker.Subscribe();

        _ = await parent.SendTextMessage("finish", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await parent.DisposeAsync();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(Reminders(Events(subscription), "parent")).IsEmpty();
        _ = await Assert.That(Payloads(repository, "parent", Event.PayloadOneofCase.TurnEnded)).IsEqualTo(1);

        await lifetime.CancelAsync();
        await otherProcesses.Settle();
        otherProvider.Dispose();
    }

    [Test]
    public async Task Interruption_bypasses_active_work_completion_enforcement(CancellationToken cancellationToken)
    {
        using var parentProvider = new HeldProvider("parent", LLMEvent.Completed("stop", 1, 0, 1, "unreachable", []));
        using var childProvider = new HeldProvider("child", LLMEvent.Completed("stop", 1, 0, 1, "child finished", []));
        var router = Router(parentProvider, childProvider);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var resources = new UserSessionResources(new StatePaths(Path.Combine(_workspace, ".state"), Path.Combine(_workspace, ".config"), Path.Combine(_workspace, ".data")), UserSessionId.Parse($"session-{Guid.NewGuid():n}"), ProjectWorkspace.FromLaunchDirectory(_workspace));
        var runner = new ProcessRunner(string.Empty);
        var repository = new EventRepository(_database);
        var factory = new CompletionAgentSessions(
            router, runner, resources, _broker, _workspace);
        await using IAgentRegistry registry = new AgentRegistry(
            factory, _broker, repository, new TestProfileFixture().Registry, TestModels.PromptTemplates, new RetainedAgentBudget(1024), TestDiagnosticLog.Instance, lifetime.Token);
        var status = new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System, new RuntimeTreeStatusProvider(registry, TestModels.PromptTemplates));
        registry.AttachStatus(status);
        var mode = new CompletionMode(enforce: true, maxTurns: 2);
        await using var parent = Session("parent", parentProvider, router, repository, registry, runner, resources, status, mode, lifetime.Token);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            new TurnFixture(parent, router).Selection,
            "worker",
            new ModelSelector("child/model"),
            "direct-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        var queues = TestModels.ScopeOf(parent).GetService<IAgentQueues>();
        _ = queues.Create("owned-work", string.Empty);
        _ = await queues.Push("owned-work", ["pending"], QueueDirection.Back, false, cancellationToken);
        using var subscription = _broker.Subscribe();

        _ = await child.SendTextMessage("work", cancellationToken);
        await childProvider.Arrived(cancellationToken);
        _ = await parent.SendTextMessage("finish", cancellationToken);
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

    private static async Task<IReadOnlyList<PendingChildQuestionRequest>> WaitForPendingQuestions(
        IChildQuestionCoordinator coordinator,
        IAgentSession parent,
        int count,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = coordinator.PendingForParent(parent);
            if (pending.Count == count)
            {
                return pending;
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static List<Event> Events(IEventSubscription subscription)
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
        IEventRepository repository,
        string sessionId,
        Event.PayloadOneofCase payload) =>
        repository.Replay().Count(published =>
            published.AgentSessionId == sessionId && published.PayloadCase == payload);

    private static IModelRouter Router(params HeldProvider[] providers)
    {
        var models = providers.ToDictionary<ILLMProvider, string, IReadOnlyList<LLMModel>>(
            provider => provider.Id,
            provider => [new LLMModel("model", provider.Id)],
            StringComparer.Ordinal);
        var registry = new ProviderRegistry(providers, models);
        var aliases = new ModelAliasCatalog(registry, []);
        return new ModelRouter(registry, new ModelRouting(aliases, $"{providers[0].Id}/model"));
    }

    private IAgentSession Session(
        string sessionId,
        HeldProvider provider,
        IModelRouter router,
        IEventRepository repository,
        IAgentRegistry registry,
        ProcessRunner runner,
        UserSessionResources resources,
        IRuntimeStatus status,
        IMode mode,
        CancellationToken lifetime)
    {
        var identity = AgentIdentity.Main(sessionId, sessionId, TestModels.PromptTemplates);
        var rootScope = TestAgentSessionScope.BuildWithResources(
            identity,
            AgentSessionParentLink.Root(),
            registry,
            TestModels.PromptTemplates,
            resources,
            runner,
            TestDiagnosticLog.Instance,
            (sessionParentScope, owningScope, children, childQuestions) =>
            {
                var exitReminder = new ExitReminder(repository, TestModels.PromptTemplates, identity.SessionId);
                return new AgentSession(identity, sessionParentScope, new ModelSelector($"{provider.Id}/model"), router, _broker, repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, _workspace, _workspace), new ToolOutputBlobStore(_workspace), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, mode, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(children, identity), new ProcessActiveWorkBlocker(owningScope.Processes), new QueueActiveWorkBlocker(owningScope.GetService<IAgentQueues>(), TestModels.PromptTemplates)], TestModels.PromptTemplates), exitReminder, repository, _broker).Callbacks, new SecurityProfileTestFixture(mode.Profile.SecurityProfile).Security, status, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, lifetime);
            },
            lifetime);
        TestModels.RegisterScope(rootScope);
        registry.RegisterRootScope(rootScope);
        _rootScopes.Add(rootScope);
        return rootScope.Session;
    }

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

    private sealed class CompletionMode(bool enforce, int maxTurns) : IMode
    {
        public int Completions { get; private set; }

        public IAgentProfile Profile { get; } = new AgentProfile(
            "test",
            new ProfileConfig("Test prompt.", string.Empty, null, maxTurns, 0, false, enforce, false, false, []),
            [],
            [],
            new HashSet<string>(StringComparer.Ordinal));

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
        IModelRouter router,
        ProcessRunner runner,
        UserSessionResources resources,
        IEventBroker broker,
        string workspace) : IAgentSessionFactory
    {
        public IEventRepository PrepareHistory(string agentSessionId, IEventRepository repository) =>
            repository.BindAgentHistory(new AgentHistoryFile(resources, agentSessionId));

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
            CancellationToken lifetime)
        {
            var scope = TestAgentSessionScope.BuildWithResources(
                identity,
                parentLink,
                registry,
                TestModels.PromptTemplates,
                resources,
                runner,
                TestDiagnosticLog.Instance,
                (sessionParentScope, owningScope, children, scopedChildQuestions) =>
                {
                    var exitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
                    IAgentSession session = new AgentSession(
                    identity,
                    sessionParentScope,
                    model,
                    router,
                    broker,
                    eventRepository,
                    [],
                    TestModels.EmptyToolDefinitions,
                    TestModels.MaterializePrompt(identity, workspace, workspace),
                    new ToolOutputBlobStore(workspace),
                    TestModels.CompactionGroupBlobs(),
                    new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates),
                    new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null),
                    new ContextCadence(),
                    TestModels.PromptTemplates,
                    scopedChildQuestions,
                    exitReminder,
                    mode,
                    new TestCompletionCallbacksFixture(scopedChildQuestions, new ActiveWorkCompletionReminder([new ChildAgentActiveWorkBlocker(children, identity), new ProcessActiveWorkBlocker(owningScope.Processes), new QueueActiveWorkBlocker(owningScope.GetService<IAgentQueues>(), TestModels.PromptTemplates)], TestModels.PromptTemplates), exitReminder, eventRepository, eventBroker).Callbacks,
                    new SecurityProfileTestFixture(securityProfile).Security,
                    status,
                    new AgentSessionActivity(TimeProvider.System),
                    TestDiagnosticLog.Instance,
                    lifetime);
                    return session;
                },
                lifetime);
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

        public IReadOnlyList<LLMModel> SeedModels() => [];

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
                    ? LLMEvent.Completed("stop", 1, 0, 1, "nothing scripted", [])
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

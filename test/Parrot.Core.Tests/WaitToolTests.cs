using Parrot.Agent;
using Parrot.AgentTasks;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.Security;
using Parrot.Skills;
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
        var queues = scope.Queues;
        _ = queues.Create("work", "queued work");
        _ = await queues.Push("work", ["item"], QueueDirection.Back, false, cancellationToken);
        _ = scope.Processes.StartUnattributed("process", "sleep 60", ProcessEnvironmentOverrides.Empty, session, SecurityProfile.Compose(readOnly: false, [], [], []), ShellProcessTerminalMode.Pipe);
        using var childProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        await using var childScope = BuildSession(childProvider, [], new EventRepository(_database), registry, AgentIdentity.Child("child", "agent", "main", "worker", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates), AgentSessionParentLink.Child(scope, AgentCompletionDeliveryPolicy.RetainedOnly), QueueResources("agent"), TestDiagnosticLog.Instance);
        _ = await childScope.Session.Send([ConversationPart.TextPart("work")], "child-message", Delivery.Steer, cancellationToken);
        await childProvider.Arrived(cancellationToken);
        var selection = new SelectionFixture(provider).Selection;
        ITool tool = new WaitTool(
            new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System),
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
            _ = await Assert.That((await tool.Execute(new ToolInvocation("test-call", arguments), new SelectionFixture(provider).Selection, cancellationToken)).Text).StartsWith("error:");
        }

        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), selection, cancellationToken);
        await time.WaitForTimer(cancellationToken);
        time.Advance(TimeSpan.FromSeconds(10));
        var estimatedTokens = session.EstimateContext(selection).EstimatedTokens;
        _ = await Assert.That((await waiting).Text).IsEqualTo(
            $"""
            Wait timed out after 10000 ms.

            Runtime:
            - agent: main
              - queue: work (1 items, description: "queued work")
              - process: agent/process (shell, running, name: process)
              - agent: worker (running)

            Context: unavailable ({estimatedTokens} estimated tokens / 0 limit); reminders every 10%; automatic compaction at 90%.
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
        await using var unobservedRegistry = new UnobservedRegistry(registry);
        ITool tool = new WaitTool(
            new RuntimeStatus(unobservedRegistry, TestModels.PromptTemplates, TimeProvider.System),
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

        _ = await Assert.That((await tool.Execute(new ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, cancellationToken)).Text).IsEqualTo("wait interrupted");
    }

    [Test]
    public async Task Agent_completion_identifies_the_agent_that_interrupted_wait(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], selectedRepository: null, registry);
        var session = scope.Session;
        await using var unobservedRegistry = new UnobservedRegistry(registry);
        ITool tool = new WaitTool(
            new RuntimeStatus(unobservedRegistry, TestModels.PromptTemplates, TimeProvider.System),
            session,
            TimeProvider.System);
        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, cancellationToken);
        await WaitUntil(session.IsWaitingForIncomingInput, cancellationToken);

        await session.ReceiveAgentCompletion("researcher", "completed", cancellationToken);

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
        await using var unobservedRegistry = new UnobservedRegistry(registry);
        ITool tool = new WaitTool(
            new RuntimeStatus(unobservedRegistry, TestModels.PromptTemplates, TimeProvider.System),
            session,
            TimeProvider.System);
        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, cancellationToken);
        await WaitUntil(session.IsWaitingForIncomingInput, cancellationToken);

        await session.ReceiveAgentTaskCompletion(
            "graph-call",
            "completed",
            Identifier.MessageId(),
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
    public async Task AgentTask_completion_spills_oversized_results_and_errors(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var repository = new EventRepository(_database);
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], repository, registry);
        var session = scope.Session;
        var blobDirectory = Path.Combine(_root, "agent-task-blobs");
        IAgentTaskRunCompletion completion = new AgentTaskRunCompletion(
            session,
            new ToolOutputBlobStore(blobDirectory),
            TestModels.PromptTemplates);

        foreach (var terminal in new[]
        {
            new AgentTaskRunTerminal(
                "result-call",
                "result-message",
                AgentTaskExecutionStatus.Succeeded,
                new string('r', ToolOutputBlobStore.MaximumInlineBytes + 1),
                string.Empty),
            new AgentTaskRunTerminal(
                "error-call",
                "error-message",
                AgentTaskExecutionStatus.Failed,
                string.Empty,
                new string('e', ToolOutputBlobStore.MaximumInlineBytes + 1)),
        })
        {
            await completion.Deliver(terminal, cancellationToken);
            var input = repository.Replay().Single(published =>
                published.AgentSessionId == session.SessionId
                && published.PayloadCase == Event.PayloadOneofCase.InputAdmitted
                && published.InputAdmitted.MessageId == terminal.CompletionMessageId).InputAdmitted;
            _ = await Assert.That(input.Content).Contains("Tool output exceeded 64 KiB and was saved to ");
            var notice = input.Content.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Single(line => line.StartsWith("Tool output exceeded", StringComparison.Ordinal));
            var path = notice[(notice.IndexOf("saved to ", StringComparison.Ordinal) + "saved to ".Length)..]
                .TrimEnd('.');
            _ = await Assert.That(File.Exists(path)).IsTrue();
            _ = await Assert.That((await File.ReadAllTextAsync(path, cancellationToken)).Length)
                .IsEqualTo(ToolOutputBlobStore.MaximumInlineBytes + 1);
        }
    }

    [Test]
    public async Task Process_completion_identifies_the_process_that_interrupted_wait(CancellationToken cancellationToken)
    {
        var provider = new UnusedProvider();
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], selectedRepository: null, registry);
        var session = scope.Session;
        await using var unobservedRegistry = new UnobservedRegistry(registry);
        ITool tool = new WaitTool(
            new RuntimeStatus(unobservedRegistry, TestModels.PromptTemplates, TimeProvider.System),
            session,
            TimeProvider.System);
        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, cancellationToken);
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
        var registry = PrepareRegistry(new EventRepository(_database));
        await using var scope = Session(provider, [], selectedRepository: null, registry);
        var session = scope.Session;
        await using var unobservedRegistry = new UnobservedRegistry(registry);
        ITool tool = new WaitTool(
            new RuntimeStatus(unobservedRegistry, TestModels.PromptTemplates, TimeProvider.System),
            session,
            TimeProvider.System);
        using var canceled = new CancellationTokenSource();
        var waiting = tool.Execute(new ToolInvocation("test-call", "{}"), new SelectionFixture(provider).Selection, canceled.Token);
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
        var registry = PrepareRegistry(new EventRepository(_database));
        var factory = new WaitToolFactory(new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System), TimeProvider.System);
        await using var scope = Session(provider, [factory], repository, registry);
        var session = scope.Session;

        _ = await session.Send(
            [ConversationPart.TextPart("first")], "message-1", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await WaitUntil(session.IsWaitingForIncomingInput, cancellationToken);
        _ = await session.Send(
            [ConversationPart.TextPart("first")], "message-1", Delivery.Steer, cancellationToken);
        await Task.Delay(20, cancellationToken);
        _ = await Assert.That(session.IsWaitingForIncomingInput()).IsTrue();
        _ = await session.Send(
            [ConversationPart.TextPart("wakeup")], "message-2", Delivery.Steer, cancellationToken);
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

    [Test]
    public async Task Child_listener_wakes_and_drains_a_direct_parents_queue(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var repository = new EventRepository(_database);
        var resources = new UserSessionResources(
            new StatePaths(_root, Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse("parent-listener-queues"),
            ProjectWorkspace.FromLaunchDirectory(_root));
        using var diagnostics = FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        var registry = PrepareRegistry(repository);
        await using var parent = BuildSession(new UnusedProvider(), [], repository, registry, AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates), AgentSessionParentLink.Root(), resources, diagnostics);
        var parentQueues = parent.Queues;
        await using var childScope = BuildSession(provider, [], repository, registry, AgentIdentity.Child("child", "parent", "parent", "child", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates), AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly), resources, diagnostics);
        var child = childScope.Session;
        var childQueues = childScope.Queues;
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
        await child.DisposeAsync();
        var log = await File.ReadAllTextAsync(resources.LogPath, cancellationToken);
        _ = await Assert.That(log).Contains("event=\"delivery_completed\"").And.Contains("outcome=\"delivered\"")
            .And.Contains("agent=\"child\"").And.DoesNotContain("parent-work").And.DoesNotContain("from-parent");
    }

    [Test]
    public async Task Competing_listeners_deliver_one_item_to_only_one_wait(CancellationToken cancellationToken)
    {
        using var firstProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        using var secondProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var repository = new EventRepository(_database);
        var resources = QueueResources("competing-listener-queues");
        var registry = PrepareRegistry(repository);
        await using var parent = BuildSession(new UnusedProvider(), [], repository, registry, AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates), AgentSessionParentLink.Root(), resources, TestDiagnosticLog.Instance);
        var parentQueues = parent.Queues;
        await using var firstScope = BuildSession(firstProvider, [], repository, registry, AgentIdentity.Child("first-child", "parent", "parent", "first", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates), AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly), resources, TestDiagnosticLog.Instance);
        var first = firstScope.Session;
        var firstQueues = firstScope.Queues;
        await using var secondScope = BuildSession(secondProvider, [], repository, registry, AgentIdentity.Child("second-child", "parent", "parent", "second", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates), AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly), resources, TestDiagnosticLog.Instance);
        var second = secondScope.Session;
        var secondQueues = secondScope.Queues;
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
            await first.DisposeAsync();
        }
        else
        {
            _ = await Assert.That((await secondWaiting)?.Kind).IsEqualTo(IncomingActivityKind.Input);
            await firstWaitCancellation.CancelAsync();
            _ = await Assert.That(firstWaiting).Throws<OperationCanceledException>();
            secondProvider.Release();
            await second.DisposeAsync();
        }
    }

    [Test]
    public async Task Disabling_one_listener_leaves_another_listener_active(CancellationToken cancellationToken)
    {
        using var disabledProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        using var activeProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var repository = new EventRepository(_database);
        var resources = QueueResources("disabled-listener-queues");
        var registry = PrepareRegistry(repository);
        await using var parent = BuildSession(new UnusedProvider(), [], repository, registry, AgentIdentity.Main("parent", "parent", TestModels.PromptTemplates), AgentSessionParentLink.Root(), resources, TestDiagnosticLog.Instance);
        var parentQueues = parent.Queues;
        await using var disabledScope = BuildSession(disabledProvider, [], repository, registry, AgentIdentity.Child("disabled-child", "parent", "parent", "disabled", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates), AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly), resources, TestDiagnosticLog.Instance);
        var disabled = disabledScope.Session;
        var disabledQueues = disabledScope.Queues;
        await using var activeScope = BuildSession(activeProvider, [], repository, registry, AgentIdentity.Child("active-child", "parent", "parent", "active", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates), AgentSessionParentLink.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly), resources, TestDiagnosticLog.Instance);
        var active = activeScope.Session;
        var activeQueues = activeScope.Queues;
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
        await active.DisposeAsync();
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
        var profiles = new TestProfileFixture().Registry;
        var modes = new ModeRegistry(profiles, ModeRegistry.Build);
        await using var owner = await AgentUserSession.Create(
            "user",
            "main",
            router.Resolve(model.Selector),
            "build",
            Resources("user"),
            sessions,
            new UserSessionModes(modes, TestModels.PromptTemplates, Path.Combine(_root, "plans")),
            TestModels.PromptTemplates,
            profiles,
            SkillCatalogFactory(),
            interactivePermissions: false,
            TimeSpan.FromSeconds(1),
            TimeProvider.System,
            static () => new EventBroker());

        _ = await owner.SendText("first", "message", Delivery.Steer, cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await sessions.WaitForSession(cancellationToken);
        var agentQueues = await sessions.WaitForQueues(cancellationToken);
        _ = agentQueues.Create("ignored", string.Empty);
        _ = await agentQueues.Push("ignored", ["no wake"], QueueDirection.Back, false, cancellationToken);
        _ = agentQueues.Create("work", string.Empty);
        _ = await agentQueues.Listen("work", true, cancellationToken);
        _ = await agentQueues.Push("work", ["queued"], QueueDirection.Back, false, cancellationToken);
        _ = await Assert.That(agentQueues.Get("work").Size).IsEqualTo(1);

        provider.Release();
        await provider.Arrived(cancellationToken);

        _ = await Assert.That(string.Join('\n', provider.Requests[1].Messages.Select(message => message.Content)))
            .Contains("Queue notification from \"work\":\n\nqueued");
        _ = await Assert.That(agentQueues.Get("work").Size).IsEqualTo(0);
        _ = await Assert.That(agentQueues.Get("ignored").Size).IsEqualTo(1);
        provider.Release();
    }

    private static async Task WaitUntil(Func<bool> predicate, CancellationToken cancellationToken)
    {
        while (!predicate())
        {
            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private static SkillCatalogFactory SkillCatalogFactory()
    {
        var configuration = Configuration.Load(
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"), "config.yaml"),
            Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"), "predefined.yaml"));
        return new SkillCatalogFactory(configuration, Path.GetTempPath(), Path.Combine(Path.GetTempPath(), "packaged-skills"));
    }

    private SessionResourceLease Resources(string ownerId)
    {
        var resources = new UserSessionResources(
            new StatePaths(_root, Path.Combine(_root, "config"), Path.Combine(_root, "data")),
            UserSessionId.Parse(ownerId),
            ProjectWorkspace.FromLaunchDirectory(_root));
        var diagnostics = (IDiagnosticLog?)FileDiagnosticLog.OpenSession(resources, "test", TextWriter.Null, TimeProvider.System);
        try
        {
            var lease = SessionResourceLease.Own(resources, _database, diagnostics ?? throw new InvalidOperationException("Missing diagnostics"));
            diagnostics = null;
            return lease;
        }
        finally
        {
            diagnostics?.Dispose();
        }
    }

    private string CreateSandboxPassThrough()
    {
        if (!OperatingSystem.IsLinux())
        {
            throw new PlatformNotSupportedException();
        }

        var path = Path.Combine(_root, $"sandbox-{Guid.NewGuid():n}");
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

    private UserSessionResources QueueResources(string sessionId) => new(
        new StatePaths(_root, Path.Combine(_root, "config"), Path.Combine(_root, "data")),
        UserSessionId.Parse(sessionId),
        ProjectWorkspace.FromLaunchDirectory(_root));

    private TestAgentSessionScope Session(
        ILLMProvider provider,
        IReadOnlyList<IToolFactory> tools,
        IEventRepository? selectedRepository,
        IAgentRegistry registry) =>
        BuildSession(provider, tools, selectedRepository ?? new EventRepository(_database), registry, AgentIdentity.Main("agent", "main", TestModels.PromptTemplates), AgentSessionParentLink.Root(), QueueResources("agent"), TestDiagnosticLog.Instance);

    private TestAgentSessionScope BuildSession(
        ILLMProvider provider,
        IReadOnlyList<IToolFactory> tools,
        IEventRepository repository,
        IAgentRegistry registry,
        AgentIdentity identity,
        AgentSessionParentLink parentLink,
        UserSessionResources resources,
        IDiagnosticLog diagnostics)
    {
        var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
        var status = registry.RequireStatus();
        var scope = TestAgentSessionScope.BuildWithResources(
            identity,
            parentLink,
            registry,
            TestModels.PromptTemplates,
            resources,
            new ProcessRunner(CreateSandboxPassThrough()),
            diagnostics,
            (sessionParentScope, owningScope, children, childQuestions) =>
            {
                var exitReminder = new ExitReminder(repository, TestModels.PromptTemplates, identity.SessionId);
                IAgentSession session = new AgentSession(identity, sessionParentScope, new ModelSelector(model.Selector), TestModels.Route(model), _broker, repository, tools, tools.Count == 0 ? TestModels.EmptyToolDefinitions : new TestToolDefinitionsFixture("wait").Definitions, TestModels.MaterializePrompt(identity, _root, _root), new ToolOutputBlobStore(Path.Combine(_root, "blobs")), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, new TestProfileFixture().Mode, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder(children, owningScope.Processes, owningScope.Queues, TestModels.PromptTemplates, null), exitReminder, repository, _broker).Callbacks, new SecurityProfileTestFixture(SecurityProfile.Compose(readOnly: false, [], [], [])).Security, status, owningScope.Queues, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, CancellationToken.None);
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
        registry.AttachStatus(new RuntimeStatus(registry, TestModels.PromptTemplates, TimeProvider.System));
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
                new TestProfileFixture().Mode,
                SecurityProfile.Compose(readOnly: false, [], [], []));
        }

        public AgentTurnSelection Selection { get; }
    }

    private sealed class UnobservedRegistry(IAgentRegistry registry) : IAgentRegistry
    {
        public CancellationToken ChildLifetime => registry.ChildLifetime;

        public bool IsAccepting => registry.IsAccepting;

        public IReadOnlyList<IAgentSessionScope> SnapshotScopes() =>
            throw new InvalidOperationException("Runtime status was observed before timeout.");

        public void AttachStatus(IRuntimeStatus status) => registry.AttachStatus(status);

        public void RegisterRootScope(IAgentSessionScope scope) => registry.RegisterRootScope(scope);

        public void UnregisterRootScope(IAgentSessionScope scope) => registry.UnregisterRootScope(scope);

        public IReadOnlyList<ActiveWorkObservation> Active() => registry.Active();

        public IReadOnlyList<ActiveAgentSnapshot> ActiveSnapshot() => registry.ActiveSnapshot();

        public ValueTask BeginShutdown() => registry.BeginShutdown();

        public IAgentProfile ResolveChildProfile(string profileId) => registry.ResolveChildProfile(profileId);

        public RetainedAgentReservation ReserveRetainedAgent() => registry.ReserveRetainedAgent();

        public IRuntimeStatus RequireStatus() => registry.RequireStatus();

        public bool ContainsScope(IAgentSessionScope candidate) => registry.ContainsScope(candidate);

        public IAgentSessionScope? FindScope(string sessionId) => registry.FindScope(sessionId);

        public IAgentSessionScope CreateChildScope(AgentIdentity identity, AgentSessionParentLink parentLink, ModelSelector model, IMode mode, SecurityProfile securityProfile, IRuntimeStatus status, IEventRepository childHistory, CancellationToken childLifetime) =>
            registry.CreateChildScope(identity, parentLink, model, mode, securityProfile, status, childHistory, childLifetime);

        public IEventRepository InitializeChildHistory(string parentSessionId, string childSessionId, HistoryForkBoundary boundary, HistoryForkSelection fork) =>
            registry.InitializeChildHistory(parentSessionId, childSessionId, boundary, fork);

        public ValueTask DisposeAsync() => registry.DisposeAsync();
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
        public IEventRepository PrepareHistory(string agentSessionId, IEventRepository repository) =>
            throw new NotSupportedException("This test session does not support spawning subagents.");

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
            throw new NotSupportedException("This test session does not support spawning subagents.");
    }

    private sealed class WaitAgentSessions(IModelRouter router, TimeProvider timeProvider, string root) : IAgentSessionFactorySource
    {
        private readonly TaskCompletionSource<IAgentSession> _created = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<IAgentQueues> _createdQueues = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAgentSessionFactory Create(IUserSession owner) => new Factory(this, owner, router, timeProvider, root);

        public async Task<IAgentSession> WaitForSession(CancellationToken cancellationToken) =>
            await _created.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        public async Task<IAgentQueues> WaitForQueues(CancellationToken cancellationToken) =>
            await _createdQueues.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

        private sealed class Factory(
            WaitAgentSessions source,
            IUserSession owner,
            IModelRouter router,
            TimeProvider timeProvider,
            string root) : IAgentSessionFactory
        {
            public IEventRepository PrepareHistory(string agentSessionId, IEventRepository repository) =>
                repository.BindAgentHistory(new AgentHistoryFile(owner.Resources, agentSessionId));

            public IAgentSessionScope Create(
                AgentIdentity identity,
                AgentSessionParentLink parentLink,
                ModelSelector model,
                IEventBroker eventBroker,
                IEventRepository eventRepository,
                IMode mode,
                SecurityProfile securityProfile,
                Parrot.Statuses.IRuntimeStatus status,
                IAgentRegistry registry,
                CancellationToken lifetime)
            {
                return TestAgentSessionScope.BuildWithResources(
                    identity,
                    parentLink,
                    registry,
                    TestModels.PromptTemplates,
                    owner.Resources,
                    new ProcessRunner(string.Empty),
                    owner.Diagnostics,
                    (sessionParentScope, owningScope, children, childQuestions) =>
                    {
                        var exitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
                        IAgentSession session = new AgentSession(identity, sessionParentScope, model, router, eventBroker, eventRepository, [new WaitToolFactory(status, timeProvider)], new TestToolDefinitionsFixture("wait").Definitions, TestModels.MaterializePrompt(identity, root, root), new ToolOutputBlobStore(Path.Combine(root, "blobs")), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(TestDiagnosticLog.Instance, "agent-test", null), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, mode, new TestCompletionCallbacksFixture(childQuestions, new ActiveWorkCompletionReminder(children, owningScope.Processes, owningScope.Queues, TestModels.PromptTemplates, null), exitReminder, eventRepository, eventBroker).Callbacks, new SecurityProfileTestFixture(securityProfile).Security, status, owningScope.Queues, new AgentSessionActivity(TimeProvider.System), TestDiagnosticLog.Instance, lifetime);
                        _ = source._createdQueues.TrySetResult(owningScope.Queues);
                        _ = source._created.TrySetResult(session);
                        return session;
                    },
                    lifetime);
            }
        }
    }
}

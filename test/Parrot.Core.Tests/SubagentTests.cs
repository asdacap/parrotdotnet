using System.Collections.Concurrent;
using System.Text.Json;
using Parrot.Agent;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Process;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.Security;
using Parrot.State;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed partial class SubagentTests : IAsyncDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();
    private readonly EventRepository _repository;
    private readonly List<IAgentSessionScope> _rootScopes = [];

    public SubagentTests() => _repository = new EventRepository(_database);

    public async ValueTask DisposeAsync()
    {
        foreach (var rootScope in _rootScopes)
        {
            TestModels.UnregisterScope(rootScope);
            await rootScope.DisposeAsync().ConfigureAwait(false);
        }

        _broker.Dispose();
        _database.Dispose();
    }

    [Test]
    public async Task Spawn_returns_immediately_and_wait_retains_the_terminal_result(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "child says hi", []));
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        var spawn = new AgentSpawnTool(TestModels.ScopeOf(parent), Router(provider));

        var startedJson = (await spawn.Execute(
            new ToolInvocation(
                "test-call",
                """{"prompt":"do the subtask","agent":"worker","name":"  Child Helper!  ","scope":"Inspect only the storage layer."}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        using var started = JsonDocument.Parse(startedJson);
        var sessionId = started.RootElement.GetProperty("session_id").GetString() ?? string.Empty;

        _ = await Assert.That(started.RootElement.GetProperty("name").GetString()).IsEqualTo("child-helper");
        _ = await Assert.That(started.RootElement.GetProperty("status").GetString()).IsEqualTo("running");
        _ = await Assert.That(started.RootElement.GetProperty("depth").GetInt32()).IsEqualTo(1);
        _ = await Assert.That(sessionId).StartsWith("agent-session-");
        await provider.Arrived(cancellationToken);

        var child = Resolver(parent, registry).ResolveStatusTarget(sessionId);
        var yielded = await child.Wait(1, cancellationToken);
        _ = await Assert.That(yielded.Yielded).IsTrue();
        _ = await Assert.That(yielded.Status).IsEqualTo(AgentTaskStatus.Running);

        provider.Release();
        var completed = await child.Wait(0, cancellationToken);
        var retained = await child.Wait(0, cancellationToken);

        _ = await Assert.That(completed.SessionId).IsEqualTo(sessionId);
        _ = await Assert.That(completed.Status).IsEqualTo(AgentTaskStatus.Succeeded);
        _ = await Assert.That(completed.ElapsedMilliseconds >= 0).IsTrue();
        _ = await Assert.That(completed.Output).IsEqualTo("child says hi");
        _ = await Assert.That(retained.Output).IsEqualTo("child says hi");
        _ = await Assert.That(sessions.Identities.Single()?.Name).IsEqualTo("child-helper");

        var lifecycle = _repository.Replay()
            .Where(published => published.PayloadCase is Event.PayloadOneofCase.AgentStarted
                or Event.PayloadOneofCase.AgentFinished
                or Event.PayloadOneofCase.AgentFailed)
            .ToArray();
        _ = await Assert.That(string.Join(",", lifecycle.Select(published => published.PayloadCase)))
            .IsEqualTo("AgentStarted,AgentFinished");
        _ = await Assert.That(lifecycle.All(published => published.AgentSessionId == sessionId)).IsTrue();
        _ = await Assert.That(lifecycle[0].AgentStarted.ParentAgentSessionId).IsEqualTo("agent");
        _ = await Assert.That(lifecycle[0].AgentStarted.Name).IsEqualTo("child-helper");
        _ = await Assert.That(lifecycle[1].AgentFinished.ParentAgentSessionId).IsEqualTo("agent");
        _ = await Assert.That(lifecycle[1].AgentFinished.Name).IsEqualTo("child-helper");
        _ = await Assert.That(lifecycle[1].AgentFinished.HasElapsedMs).IsTrue();
        _ = await Assert.That(lifecycle[1].AgentFinished.ElapsedMs >= 0).IsTrue();

        var systemPrompt = provider.Requests[0].Instructions;
        _ = await Assert.That(provider.Requests[0].Messages)
            .DoesNotContain(message => message.Role == LLMRole.System
                && message.Content.Contains($"Child agent session: {sessionId}", StringComparison.Ordinal));
        _ = await Assert.That(systemPrompt).Contains($"Child agent session: {sessionId}");
        _ = await Assert.That(systemPrompt).Contains("Parent agent session: agent");
        _ = await Assert.That(systemPrompt).Contains("Parent agent name: ");
        _ = await Assert.That(systemPrompt).Contains("Child agent name: child-helper");
        _ = await Assert.That(systemPrompt).Contains(
            "## Scope\n\n"
            + "### Self\n"
            + "Inspect only the storage layer.");
    }

    [Test]
    public async Task Send_and_wait_returns_output_or_reports_terminal_and_caller_cancellation(
        CancellationToken cancellationToken)
    {
        using var successProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "result", []));
        await using var successRegistry = TestModels.Registry(
            new TestAgentSessions(Router(successProvider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var success = Session(successProvider, 0, "success", successRegistry, cancellationToken);
        var successTask = success.SendAndWaitForResult("work", cancellationToken);
        await successProvider.Arrived(cancellationToken);
        successProvider.Release();
        _ = await Assert.That(await successTask).IsEqualTo("result");

        var failureProvider = new TerminalFailureProvider("provider failed");
        await using var failureRegistry = TestModels.Registry(
            new TestAgentSessions(Router(failureProvider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var failure = Session(failureProvider, 0, "failure", failureRegistry, cancellationToken);
        AgentExecutionException? failed = null;
        try
        {
            _ = await failure.SendAndWaitForResult("work", cancellationToken);
        }
        catch (AgentExecutionException exception)
        {
            failed = exception;
        }

        var failedExecution = failed ?? throw new InvalidOperationException("Expected terminal failure.");
        _ = await Assert.That(failedExecution.Status).IsEqualTo(AgentTaskStatus.Failed);
        _ = await Assert.That(failedExecution.Message).IsEqualTo("provider failed");

        using var canceledProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "unreachable", []));
        await using var canceledRegistry = TestModels.Registry(
            new TestAgentSessions(Router(canceledProvider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var canceled = Session(canceledProvider, 0, "canceled", canceledRegistry, cancellationToken);
        var canceledTask = canceled.SendAndWaitForResult("work", cancellationToken);
        await canceledProvider.Arrived(cancellationToken);
        await canceled.Interrupt(CancellationToken.None);
        var terminalCanceled = await Assert.That(canceledTask).Throws<AgentExecutionException>();
        _ = await Assert.That(terminalCanceled?.Status).IsEqualTo(AgentTaskStatus.Canceled);
        _ = await Assert.That(terminalCanceled?.Message).IsEqualTo("interrupted");

        using var callerProvider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "unreachable", []));
        await using var callerRegistry = TestModels.Registry(
            new TestAgentSessions(Router(callerProvider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var caller = Session(callerProvider, 0, "caller", callerRegistry, cancellationToken);
        using var callerCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var callerTask = caller.SendAndWaitForResult("work", callerCancellation.Token);
        await callerProvider.Arrived(cancellationToken);
        await callerCancellation.CancelAsync();
        _ = await Assert.That(callerTask).Throws<OperationCanceledException>();
        await caller.Interrupt(CancellationToken.None);
    }

    [Test]
    public async Task Send_and_wait_serializes_full_executions_and_returns_each_result(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "second", []),
            LLMEvent.Completed("stop", 1, 0, 1, "third", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var session = Session(provider, 0, "serialized", registry, cancellationToken);

        var first = session.SendAndWaitForResult("first prompt", cancellationToken);
        await provider.Arrived(cancellationToken);
        var second = session.SendAndWaitForResult("second prompt", cancellationToken);
        var third = session.SendAndWaitForResult("third prompt", cancellationToken);

        provider.Release();
        _ = await Assert.That(await first).IsEqualTo("first");
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
        _ = await Assert.That(provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content)
            .IsEqualTo("second prompt");
        _ = await Assert.That(second.IsCompleted).IsFalse();
        _ = await Assert.That(third.IsCompleted).IsFalse();

        provider.Release();
        _ = await Assert.That(await second).IsEqualTo("second");
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(3);
        _ = await Assert.That(provider.Requests[2].Messages.Last(message => message.Role == LLMRole.User).Content)
            .IsEqualTo("third prompt");
        _ = await Assert.That(third.IsCompleted).IsFalse();

        provider.Release();
        _ = await Assert.That(await third).IsEqualTo("third");
        var events = _repository.Replay()
            .Where(published => published.AgentSessionId == session.SessionId)
            .ToArray();
        _ = await Assert.That(events.Count(published => published.PayloadCase == Event.PayloadOneofCase.TurnStarted))
            .IsEqualTo(3);
        _ = await Assert.That(events.Count(published => published.PayloadCase == Event.PayloadOneofCase.TurnEnded))
            .IsEqualTo(3);
        _ = await Assert.That(events.Count(published => published.PayloadCase == Event.PayloadOneofCase.AgentFinished))
            .IsEqualTo(3);
    }

    [Test]
    public async Task Send_and_wait_continues_after_a_failed_predecessor(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "second", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var session = Session(provider, 0, "failed-predecessor", registry, cancellationToken);

        var first = session.SendAndWaitForResult("first prompt", cancellationToken);
        await provider.Arrived(cancellationToken);
        var second = session.SendAndWaitForResult("second prompt", cancellationToken);
        await session.Interrupt(CancellationToken.None);

        var interrupted = await Assert.That(first).Throws<AgentExecutionException>();
        _ = await Assert.That(interrupted?.Status).IsEqualTo(AgentTaskStatus.Canceled);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[1].Messages.Last(message => message.Role == LLMRole.User).Content)
            .IsEqualTo("second prompt");
        provider.Release();
        _ = await Assert.That(await second).IsEqualTo("second");
    }

    [Test]
    public async Task Spawn_full_fork_seeds_child_before_its_first_prompt(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        _repository.AppendConversation(
            new Event { Id = "parent-context", AgentSessionId = parent.SessionId },
            ConversationOrigin.UserInput,
            LLMRole.User,
            [ConversationPart.TextPart("inherited context")],
            [],
            string.Empty);
        _repository.AppendConversation(
            new Event { Id = "spawn-batch", AgentSessionId = parent.SessionId },
            ConversationOrigin.Model,
            LLMRole.Assistant,
            [ConversationPart.TextPart(string.Empty)],
            [new LLMToolCall("spawn-call", "agent_spawn", "{}")],
            string.Empty);
        var spawnSequence = _repository.Conversation(parent.SessionId)[^1].Sequence;

        var result = await new AgentSpawnTool(TestModels.ScopeOf(parent), Router(provider)).Execute(
            new ToolInvocation(
                "spawn-call",
                "{\"prompt\":\"new work\",\"agent\":\"worker\",\"fork\":\"full\"}",
                spawnSequence),
            Turn(parent, Router(provider)),
            cancellationToken);

        _ = await Assert.That(result.Text).DoesNotStartWith("error:");
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests.Single().Messages.Select(message => message.Content))
            .Contains("inherited context")
            .And.Contains("new work")
            .And.DoesNotContain(message => message.Contains("agent_spawn", StringComparison.Ordinal));
        provider.Release();
    }

    [Test]
    public async Task Friendly_names_are_scoped_to_direct_siblings(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", registry, cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", registry, cancellationToken);
        var first = TestModels.ScopeOf(firstParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            firstParent,
            Turn(firstParent, Router(provider)),
            "worker",
            firstParent.CurrentSelection().RequestedModel,
            "helper",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var sibling = TestModels.ScopeOf(firstParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            firstParent,
            Turn(firstParent, Router(provider)),
            "worker",
            firstParent.CurrentSelection().RequestedModel,
            "helper",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var otherBranch = TestModels.ScopeOf(secondParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            secondParent,
            Turn(secondParent, Router(provider)),
            "worker",
            secondParent.CurrentSelection().RequestedModel,
            "helper",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;

        _ = await Assert.That(first.Name).IsEqualTo("helper");
        _ = await Assert.That(sibling.Name).IsEqualTo("helper-2");
        _ = await Assert.That(otherBranch.Name).IsEqualTo("helper");
        _ = await Assert.That(string.Join(',', sessions.Identities.Select(identity => identity.Name)))
            .IsEqualTo("helper,helper,helper-2,helper");
        _ = await Assert.That(Resolver(firstParent, registry).ResolveStatusTarget("helper")).IsSameReferenceAs(first);
        _ = await Assert.That(Resolver(secondParent, registry).ResolveStatusTarget("helper")).IsSameReferenceAs(otherBranch);
    }

    [Test]
    public async Task Scope_changes_inherit_through_the_registry_and_use_canonical_names(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root", "root-agent", registry, cancellationToken);
        var planner = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            "  Planner!  ",
            "Plan the migration.",
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var worker = TestModels.ScopeOf(planner).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            planner,
            Turn(planner, Router(provider)),
            "worker",
            planner.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var reviewer = TestModels.ScopeOf(worker).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            worker,
            Turn(worker, Router(provider)),
            "worker",
            worker.CurrentSelection().RequestedModel,
            "reviewer",
            "Plan the migration.",
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var implementer = TestModels.ScopeOf(worker).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            worker,
            Turn(worker, Router(provider)),
            "worker",
            worker.CurrentSelection().RequestedModel,
            "implementer",
            "Implement the migration.",
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;

        _ = await Assert.That(planner.Name).IsEqualTo("planner");
        _ = await Assert.That(worker.Identity.Scope.Format(worker.Depth)).IsEqualTo(
            "## Scope\n\n"
            + "### 1st Ancestor (planner)\n"
            + "Plan the migration.");
        _ = await Assert.That(reviewer.Identity.Scope.Format(reviewer.Depth)).IsEqualTo(
            "## Scope\n\n"
            + "### 2nd Ancestor (planner)\n"
            + "Plan the migration.");
        _ = await Assert.That(implementer.Identity.Scope.Format(implementer.Depth)).IsEqualTo(
            "## Scope\n\n"
            + "### 2nd Ancestor (planner)\n"
            + "Plan the migration.\n\n"
            + "### Self\n"
            + "Implement the migration.");
    }

    [Test]
    public async Task Canonical_child_lookup_stays_global_while_send_rejects_unrelated_agents(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", registry, cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", registry, cancellationToken);
        var target = TestModels.ScopeOf(firstParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            firstParent,
            Turn(firstParent, Router(provider)),
            "worker",
            firstParent.CurrentSelection().RequestedModel,
            "helper",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;

        _ = await Assert.That(Resolver(secondParent, registry).ResolveStatusTarget(target.SessionId)).IsSameReferenceAs(target);
        var unrelated = await Assert.That(() => Resolver(secondParent, registry).ResolveRecipient(target.SessionId))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(unrelated?.Message).IsEqualTo("only parent/child may be sent");
        _ = await Assert.That(() => Resolver(secondParent, registry).ResolveStatusTarget("helper")).Throws<AgentRegistryException>();
        _ = await Assert.That(() => Resolver(secondParent, registry).ResolveRecipient("helper")).Throws<AgentRegistryException>();
    }

    [Test]
    public async Task Resolver_rejects_an_unregistered_owner_before_canonical_or_direct_resolution(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var owner = Session(provider, 0, "owner", registry, cancellationToken);
        var ownerScope = TestModels.ScopeOf(owner);
        var direct = ownerScope.AgentSpawner.SpawnScope(new AgentLaunchRequest(
            owner,
            Turn(owner, Router(provider)),
            "worker",
            owner.CurrentSelection().RequestedModel,
            "direct-child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var otherParent = Session(provider, 0, "other-parent", registry, cancellationToken);
        var canonicalTarget = TestModels.ScopeOf(otherParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            otherParent,
            Turn(otherParent, Router(provider)),
            "worker",
            otherParent.CurrentSelection().RequestedModel,
            "canonical-target",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var resolver = Resolver(owner, registry);
        registry.UnregisterRootScope(ownerScope);
        var expectedMessage = $"parent agent scope not found: {owner.SessionId}";

        foreach (var resolution in new Func<object>[]
                 {
                     () => resolver.ResolveStatusTargetScope(canonicalTarget.SessionId),
                     () => resolver.ResolveStatusTargetScope(direct.Name),
                     () => resolver.ResolveStatusTarget(canonicalTarget.SessionId),
                     () => resolver.ResolveStatusTarget(direct.Name),
                     () => resolver.ResolveRecipient(canonicalTarget.SessionId),
                     () => resolver.ResolveRecipient(direct.Name),
                 })
        {
            var exception = await Assert.That(resolution).Throws<AgentRegistryException>();
            _ = await Assert.That(exception?.Message).IsEqualTo(expectedMessage);
        }
    }

    [Test]
    public async Task Send_resolution_prefers_canonical_ids_then_the_parent_name(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root-id", registry, cancellationToken);
        var parent = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            "parent-name",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var caller = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "caller",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var parentNameCollision = TestModels.ScopeOf(caller).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            caller,
            Turn(caller, Router(provider)),
            "worker",
            caller.CurrentSelection().RequestedModel,
            parent.Name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var canonicalCollision = TestModels.ScopeOf(caller).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            caller,
            Turn(caller, Router(provider)),
            "worker",
            caller.CurrentSelection().RequestedModel,
            parentNameCollision.SessionId,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;

        _ = await Assert.That(Resolver(caller, registry).ResolveRecipient(parent.Name)).IsSameReferenceAs(parent);
        _ = await Assert.That(Resolver(caller, registry).ResolveStatusTarget(parent.Name)).IsSameReferenceAs(parentNameCollision);
        _ = await Assert.That(canonicalCollision.Name).IsEqualTo(parentNameCollision.SessionId);
        _ = await Assert.That(Resolver(caller, registry).ResolveRecipient(canonicalCollision.Name))
            .IsSameReferenceAs(parentNameCollision);
    }

    [Test]
    public async Task Send_rejects_a_spawned_grandparent_without_exposing_its_id(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root-id", registry, cancellationToken);
        var grandparent = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            "grandparent",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var parent = TestModels.ScopeOf(grandparent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            grandparent,
            Turn(grandparent, Router(provider)),
            "worker",
            grandparent.CurrentSelection().RequestedModel,
            "parent-agent",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var sender = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "sender",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var send = new AgentSendTool(sender.Identity, Resolver(sender, registry), sender);

        var result = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{grandparent.SessionId}}","message":"skip parent"}"""),
            Turn(sender, Router(provider)),
            cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo("error: only parent/child may be sent");
        _ = await Assert.That(result).DoesNotContain(grandparent.SessionId);
        _ = await Assert.That(grandparent.IsActive()).IsFalse();
        _ = await Assert.That(provider.Requests).IsEmpty();
    }

    [Test]
    public async Task Generated_names_are_scoped_to_the_spawning_parent(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", registry, cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", registry, cancellationToken);
        var first = TestModels.ScopeOf(firstParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            firstParent,
            Turn(firstParent, Router(provider)),
            "worker",
            firstParent.CurrentSelection().RequestedModel,
            string.Empty,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var second = TestModels.ScopeOf(secondParent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            secondParent,
            Turn(secondParent, Router(provider)),
            "worker",
            secondParent.CurrentSelection().RequestedModel,
            first.Name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;

        _ = await Assert.That(second.Name).IsEqualTo(first.Name);
        _ = await Assert.That(Resolver(firstParent, registry).ResolveStatusTarget(first.Name)).IsSameReferenceAs(first);
        _ = await Assert.That(Resolver(secondParent, registry).ResolveStatusTarget(first.Name)).IsSameReferenceAs(second);
    }

    [Test]
    public async Task Send_resolves_literal_parent_before_a_direct_child_name_collision(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root-id", "root", registry, cancellationToken);
        var parent = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            "actual-parent",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var caller = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "caller",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var namedParent = TestModels.ScopeOf(caller).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            caller,
            Turn(caller, Router(provider)),
            "worker",
            caller.CurrentSelection().RequestedModel,
            "parent",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;

        _ = await Assert.That(Resolver(caller, registry).ResolveRecipient("parent")).IsSameReferenceAs(parent);
        _ = await Assert.That(Resolver(caller, registry).ResolveRecipient(parent.SessionId)).IsSameReferenceAs(parent);
        _ = await Assert.That(Resolver(caller, registry).ResolveRecipient(parent.Name)).IsSameReferenceAs(parent);
        _ = await Assert.That(Resolver(caller, registry).ResolveStatusTarget("parent")).IsSameReferenceAs(namedParent);
        _ = await Assert.That(Resolver(caller, registry).ResolveStatusTarget(namedParent.SessionId)).IsSameReferenceAs(namedParent);
    }

    [Test]
    public async Task Root_resolves_a_child_named_parent_and_otherwise_reports_not_found(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root-id", "root", registry, cancellationToken);
        var emptyRoot = Session(provider, 0, "empty-root-id", "empty-root", registry, cancellationToken);
        var child = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            "parent",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;

        _ = await Assert.That(Resolver(root, registry).ResolveRecipient("parent")).IsSameReferenceAs(child);
        _ = await Assert.That(Resolver(root, registry).ResolveStatusTarget("parent")).IsSameReferenceAs(child);
        var missing = await Assert.That(() => Resolver(emptyRoot, registry).ResolveRecipient("parent"))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(missing?.Message).IsEqualTo("child agent not found: parent");
    }

    [Test]
    public async Task Send_metadata_describes_literal_parent_and_accepted_recipient_forms(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var session = Session(provider, 0, "root-id", registry, cancellationToken);
        var send = new AgentSendTool(session.Identity, Resolver(session, registry), session);
        var root = Path.Combine(Path.GetTempPath(), "parrot-agent-send-documentation", Guid.NewGuid().ToString("N"));
        var configuration = Configuration.Load(
            Path.Combine(root, "config.yaml"),
            Path.Combine(root, "predefined_config.yaml"));
        var definitions = new ToolDefinitionCatalog(
            new Dictionary<string, ConfiguredToolDefinition>(StringComparer.Ordinal)
            {
                [send.Name] = configuration.ToolDefinitions.Definitions[send.Name],
            });
        var definition = definitions.Document([send]).Single();
        using var schema = JsonDocument.Parse(definition.ParametersJson);
        var sessionIdDescription = schema.RootElement.GetProperty("properties").GetProperty("session_id")
            .GetProperty("description").GetString();

        _ = await Assert.That(definition.Description).Contains("direct parent or direct child or to a descendant");
        _ = await Assert.That(definition.Description).Contains("sender's own descendant tree");
        _ = await Assert.That(definition.Description).Contains("slash-separated friendly-name paths");
        _ = await Assert.That(definition.Description).Contains("child/grandchild");
        _ = await Assert.That(definition.Description).Contains("paths only travel downward");
        _ = await Assert.That(sessionIdDescription).Contains("direct parent or direct child");
        _ = await Assert.That(sessionIdDescription).Contains("literal 'parent'");
        _ = await Assert.That(sessionIdDescription).Contains("direct-child friendly name");
        _ = await Assert.That(sessionIdDescription).Contains("slash-separated relative");
        _ = await Assert.That(sessionIdDescription).Contains("child/grandchild");
        _ = await Assert.That(sessionIdDescription).Contains("do not accept canonical IDs for descendants");
        _ = await Assert.That(sessionIdDescription).Contains("direct-parent alias precedence");
    }

    [Test]
    public async Task Send_resolves_descendant_paths_only_through_branch_local_friendly_names(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root-id", registry, cancellationToken);

        IAgentSessionScope Spawn(IAgentSessionScope parent, string profile, string name) => parent.AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent.Session,
            Turn(parent.Session, Router(provider)),
            profile,
            parent.Session.CurrentSelection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly));

        var rootScope = TestModels.ScopeOf(root);
        var firstScope = Spawn(rootScope, "worker", "first");
        var duplicateScope = Spawn(rootScope, "worker", "duplicate");
        var nestedDuplicateScope = Spawn(firstScope, "explorer", "duplicate");
        var parentNamedScope = Spawn(nestedDuplicateScope, "worker", "parent");
        var targetScope = Spawn(parentNamedScope, "explorer", "target");
        var unrelatedTargetScope = Spawn(duplicateScope, "explorer", "target");
        var first = firstScope.Session;
        var duplicate = duplicateScope.Session;
        var nestedDuplicate = nestedDuplicateScope.Session;
        var target = targetScope.Session;
        var unrelatedTarget = unrelatedTargetScope.Session;

        _ = await Assert.That(TestModels.ScopeOf(target)).IsSameReferenceAs(targetScope);

        _ = await Assert.That(Resolver(root, registry).ResolveRecipient("first/duplicate/parent/target"))
            .IsSameReferenceAs(target);
        _ = await Assert.That(Resolver(first, registry).ResolveRecipient("duplicate/parent/target"))
            .IsSameReferenceAs(target);
        _ = await Assert.That(Resolver(root, registry).ResolveRecipient("duplicate/target"))
            .IsSameReferenceAs(unrelatedTarget);
        _ = await Assert.That(Resolver(nestedDuplicate, registry).ResolveRecipient("parent/target"))
            .IsSameReferenceAs(target);

        var sent = (await new AgentSendTool(root.Identity, Resolver(root, registry), root).Execute(
            new ToolInvocation(
                "test-call",
                """{"session_id":"first/duplicate/parent/target","message":"deep work"}"""),
            Turn(root, Router(provider)),
            cancellationToken)).Text;
        using var sentResult = JsonDocument.Parse(sent);
        _ = await Assert.That(sentResult.RootElement.GetProperty("session_id").GetString())
            .IsEqualTo(target.SessionId);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests.Single().Messages.Select(message => message.Content))
            .Contains("deep work");
        provider.Release();
        _ = await target.Wait(0, cancellationToken);

        foreach (var path in new[]
                 {
                     "/first", "first/", "first//duplicate", "first/missing", "First/duplicate",
                     "first/./target", "first/../target", "duplicate/parent/target",
                     $"first/{nestedDuplicate.SessionId}", $"{first.SessionId}/duplicate",
                 })
        {
            var error = await Assert.That(() => Resolver(root, registry).ResolveRecipient(path)).Throws<AgentRegistryException>();
            _ = await Assert.That(error?.Message).IsEqualTo($"child agent not found: {path}");
            _ = await Assert.That(error?.Message).DoesNotContain(target.SessionId);
            _ = await Assert.That(error?.Message).DoesNotContain(unrelatedTarget.SessionId);
        }

        _ = await Assert.That(() => Resolver(root, registry).ResolveStatusTarget("first/duplicate"))
            .Throws<AgentRegistryException>();
    }

    [Test]
    public async Task Completion_automatically_starts_a_parent_follow_up(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "child result", []),
            LLMEvent.Completed("stop", 1, 0, 1, "parent result", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "helper",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;

        _ = await child.SendTextMessage("do work", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);

        var notification = string.Join('\n', provider.Requests[1].Messages.Select(message => message.Content));
        _ = await Assert.That(notification).Contains("Agent task notification");
        _ = await Assert.That(notification).Contains($"Child agent session: {child.SessionId}");
        _ = await Assert.That(notification).Contains("Child agent name: helper");
        _ = await Assert.That(notification).Contains("Status: succeeded");
        _ = await Assert.That(notification).Contains("child result");
        provider.Release();
        await parent.DisposeAsync();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Completion_during_parent_registry_teardown_does_not_follow_up_or_lose_terminal_outcome(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "unreachable", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var parentScope = TestModels.ScopeOf(parent);
        var child = parentScope.AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "helper",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;

        _ = await child.SendTextMessage("do work", cancellationToken);
        await provider.Arrived(cancellationToken);

        var shutdown = parentScope.DisposeAsync().AsTask();
        _ = await Assert.That(parentScope.ChildRegistry.IsAccepting).IsFalse();
        await shutdown;
        var completed = await child.Wait(0, cancellationToken);
        var retained = await child.Wait(0, cancellationToken);

        _ = await Assert.That(completed.Status).IsEqualTo(AgentTaskStatus.Canceled);
        _ = await Assert.That(completed.Error).IsEqualTo("interrupted");
        _ = await Assert.That(retained.Status).IsEqualTo(completed.Status);
        _ = await Assert.That(retained.Error).IsEqualTo(completed.Error);
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
    }

    [Test]
    public async Task Retained_only_completion_preserves_terminal_result_without_steering_the_parent(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "child result", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "internal-helper",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;

        _ = await child.SendTextMessage("do work", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        var completed = await child.Wait(0, cancellationToken);

        _ = await Assert.That(completed.Status).IsEqualTo(AgentTaskStatus.Succeeded);
        _ = await Assert.That(completed.Output).IsEqualTo("child result");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(1);
        _ = await Assert.That(_repository.Replay().Select(item => item.PayloadCase))
            .Contains(Event.PayloadOneofCase.AgentStarted)
            .And.Contains(Event.PayloadOneofCase.AgentFinished);
        var retained = Resolver(parent, registry).ResolveStatusTarget(child.SessionId).Activity.Capture();
        _ = await Assert.That(retained.State).IsEqualTo(DrainState.Idle);
        _ = await Assert.That(retained.TerminalOutcome?.Status).IsEqualTo(AgentExecutionStatus.Succeeded);
        _ = await Assert.That(retained.TerminalOutcome?.Output).IsEqualTo("child result");
        _ = await Assert.That(retained.Recent).Count().IsEqualTo(1);
        _ = await Assert.That(retained.Recent[0].Content).IsEqualTo("child result");
    }

    [Test]
    public async Task Nested_completion_does_not_restart_a_disposed_root(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "intermediate ready", []),
            LLMEvent.Completed("stop", 1, 0, 1, "root initial result", []),
            LLMEvent.Completed("stop", 1, 0, 1, "nested result", []),
            LLMEvent.Completed("stop", 1, 0, 1, "intermediate result", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root", registry, cancellationToken);
        var intermediate = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            "intermediate",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;
        _ = await intermediate.SendTextMessage("prepare", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        await root.DisposeAsync();
        var nested = TestModels.ScopeOf(intermediate).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            intermediate,
            Turn(intermediate, Router(provider)),
            "worker",
            intermediate.CurrentSelection().RequestedModel,
            "nested",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.Automatic)).Session;

        _ = await nested.SendTextMessage("inspect", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        var intermediateNotification = string.Join('\n', provider.Requests[3].Messages.Select(message => message.Content));
        _ = await Assert.That(intermediateNotification).Contains($"Child agent session: {nested.SessionId}");
        provider.Release();
        _ = await intermediate.Wait(0, cancellationToken);
        await root.DisposeAsync();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(4);
    }

    [Test]
    public async Task Completion_bounds_a_multibyte_terminal_error(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var identity = AgentIdentity.Child("child", "parent", "parent", "helper", 1, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates);
        var notification = AgentExecution.Failed(new string('界', 262_144)).FormatCompletion(identity, TestModels.PromptTemplates);

        _ = await Assert.That(notification).Contains("Status: failed");
        _ = await Assert.That(notification).Contains("Error:");
        _ = await Assert.That(System.Text.Encoding.UTF8.GetByteCount(notification) <= 1024 * 1024).IsTrue();
    }

    [Test]
    public async Task Spawn_resolves_the_explore_alias_to_the_canonical_child_profile(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        var spawn = new AgentSpawnTool(TestModels.ScopeOf(parent), Router(provider));

        _ = (await spawn.Execute(new ToolInvocation("test-call", """{"prompt":"inspect","agent":"explore"}"""), Turn(parent, Router(provider)), cancellationToken)).Text;
        await provider.Arrived(cancellationToken);
        provider.Release();

        _ = await Assert.That(sessions.Profiles.Single()?.Id).IsEqualTo("explorer");
    }

    [Test]
    public async Task Spawn_adapts_child_profile_to_a_noop_mode_with_restricted_security(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        parent.UpdateSelection(parent.CurrentSelection().RequestedModel, Profile("parent", readOnly: true, []));

        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var mode = sessions.Profiles.Single();
        var securityProfile = sessions.SecurityProfiles.Single();

        mode.Prepare();
        _ = await Assert.That(mode.Id).IsEqualTo("worker");
        _ = await Assert.That(mode.Prompt).IsEqualTo("You are a worker agent.");
        _ = await Assert.That(mode.MaxTurns).IsEqualTo(64);
        _ = await Assert.That(mode.SecurityProfile).IsSameReferenceAs(securityProfile);
        _ = await Assert.That(mode.SecurityProfile.ReadOnly).IsTrue();
        _ = await Assert.That(mode.Complete(child.SessionId, "message").Completion).IsNull();

        _ = await child.SendTextMessage("work", cancellationToken);
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests.Single().Instructions).Contains(
            "The following configured sandbox rules override every other prompt rule and instruction.");
        _ = await Assert.That(provider.Requests.Single().Instructions).DoesNotContain("ReadOnly:");
        provider.Release();
        await child.DisposeAsync();

        _ = await Assert.That(_repository.Replay().Any(published =>
            published.AgentSessionId == child.SessionId
            && published.PayloadCase == Event.PayloadOneofCase.PlanCompleted)).IsFalse();
    }

    [Test]
    public async Task Spawn_uses_the_selection_captured_before_parent_selection_changes(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        parent.UpdateSelection(parent.CurrentSelection().RequestedModel, Profile(ModeRegistry.Build, readOnly: false, []));
        var spawn = new AgentSpawnTool(TestModels.ScopeOf(parent), Router(provider));
        var capturedSelection = Turn(parent, Router(provider));
        parent.UpdateSelection(
            new ModelSelector("stepped/replacement"),
            parent.CurrentSelection().Profile);

        _ = (await spawn.Execute(new ToolInvocation("test-call", """{"prompt":"do the subtask","agent":"worker"}"""), capturedSelection, cancellationToken)).Text;
        await provider.Arrived(cancellationToken);
        provider.Release();

        _ = await Assert.That(sessions.Models.Single().Value).IsEqualTo("stepped/model");
        _ = await Assert.That(sessions.Profiles.Single()?.Id).IsEqualTo("worker");
    }

    [Test]
    public async Task Spawn_inherits_the_omitted_selector_and_propagates_alias_icons_in_turn_order(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "inherited", []),
            LLMEvent.Completed("stop", 1, 0, 1, "alias", []),
            LLMEvent.Completed("stop", 1, 0, 1, "canonical", []));
        var alias = new ModelAliasDefinition(
            "fast",
            "stepped/replacement",
            "Fast work",
            null,
            new ModelAliasIcon("F", ModelAliasIconColor.Gray));
        var router = Router(provider, [alias]);
        var sessions = new TestAgentSessions(router);
        await using var registry = TestModels.Registry(sessions, _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        parent.UpdateSelection(new ModelSelector("fast"), parent.CurrentSelection().Profile);
        var spawn = new AgentSpawnTool(TestModels.ScopeOf(parent), router);

        foreach (var arguments in new[]
        {
            """{"prompt":"inherit","agent":"worker"}""",
            """{"prompt":"override alias","agent":"worker","model":"fast"}""",
            """{"prompt":"override canonical","agent":"worker","model":"stepped/model"}""",
        })
        {
            _ = (await spawn.Execute(new ToolInvocation("test-call", arguments), Turn(parent, router), cancellationToken)).Text;
            await provider.Arrived(cancellationToken);
            provider.Release();
        }

        _ = await Assert.That(string.Join(',', sessions.Models.Select(model => model.Value)))
            .IsEqualTo("fast,fast,stepped/model");
        _ = await Assert.That(string.Join(',', provider.Requests.Select(request => request.Model)))
            .IsEqualTo("replacement,replacement,model");
        var statuses = provider.Requests
            .Select(request => request.Messages.Single(message =>
                message.Role == LLMRole.System
                && message.Content.Contains("Active profile:", StringComparison.Ordinal)).Content)
            .ToArray();
        _ = await Assert.That(statuses[0]).Contains("Model: fast");
        _ = await Assert.That(statuses[1]).Contains("Model: fast");
        _ = await Assert.That(statuses[0]).DoesNotContain("Model: stepped/replacement");
        _ = await Assert.That(statuses[1]).DoesNotContain("Model: stepped/replacement");
        _ = await Assert.That(statuses[2]).Contains("Model: stepped/model");
        var started = _repository.Replay()
            .Where(published => published.PayloadCase == Event.PayloadOneofCase.TurnStarted)
            .Select(published => published.TurnStarted)
            .ToArray();
        _ = await Assert.That(string.Join(',', started.Select(turn => turn.Model)))
            .IsEqualTo("stepped/replacement,stepped/replacement,stepped/model");
        _ = await Assert.That(started[0].ModelAliasIcon.Glyph).IsEqualTo("F");
        _ = await Assert.That(started[0].ModelAliasIcon.Color).IsEqualTo(TurnModelAliasIconColor.Gray);
        _ = await Assert.That(started[1].ModelAliasIcon.Glyph).IsEqualTo("F");
        _ = await Assert.That(started[1].ModelAliasIcon.Color).IsEqualTo(TurnModelAliasIconColor.Gray);
        _ = await Assert.That(started[2].ModelAliasIcon).IsNull();
    }

    [Test]
    public async Task Spawn_rejects_an_invalid_alias(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var invalid = new ModelAliasDefinition("broken", string.Empty, "Unavailable", null, null);
        var router = Router(provider, [invalid]);
        var sessions = new TestAgentSessions(router);
        await using var registry = TestModels.Registry(sessions, _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        var spawn = new AgentSpawnTool(TestModels.ScopeOf(parent), router);

        var result = (await spawn.Execute(
            new ToolInvocation(
                "test-call",
                """{"prompt":"work","agent":"worker","model":"broken"}"""),
            Turn(parent, router),
            cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo("error: model alias: alias \"broken\" is not configured");
        _ = await Assert.That(sessions.Models).IsEmpty();
    }

    [Test]
    public async Task Send_steers_running_child_and_reuses_idle_session_for_follow_up(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "steered", []),
            LLMEvent.Completed("stop", 1, 0, 1, "followed up", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)), _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        var spawned = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        _ = await spawned.SendTextMessage("initial", cancellationToken);
        var send = new AgentSendTool(parent.Identity, Resolver(parent, registry), parent);

        await provider.Arrived(cancellationToken);
        var steeredJson = (await send.Execute(
            new ToolInvocation(
                "test-call",
                """{"session_id":"worker","message":"steer now"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        using var steered = JsonDocument.Parse(steeredJson);
        _ = await Assert.That(steered.RootElement.GetProperty("session_id").GetString())
            .IsEqualTo(spawned.SessionId);
        _ = await Assert.That(steered.RootElement.GetProperty("message_id").GetString())
            .StartsWith("msg-");

        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[1].Messages.Select(message => message.Content))
            .Contains("steer now");
        provider.Release();
        var steeredResult = await spawned.Wait(0, cancellationToken);
        _ = await Assert.That(steeredResult.Output).IsEqualTo("steered");

        var followedUpJson = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"follow up"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        using var followedUp = JsonDocument.Parse(followedUpJson);
        _ = await Assert.That(followedUp.RootElement.GetProperty("session_id").GetString())
            .IsEqualTo(spawned.SessionId);
        _ = await Assert.That(followedUp.RootElement.GetProperty("status").GetString()).IsEqualTo("running");

        await provider.Arrived(cancellationToken);
        var conversation = string.Join('\n', provider.Requests[2].Messages.Select(message => message.Content));
        _ = await Assert.That(
            conversation.Contains("initial", StringComparison.Ordinal)
            && conversation.Contains("first", StringComparison.Ordinal)
            && conversation.Contains("steer now", StringComparison.Ordinal)
            && conversation.Contains("steered", StringComparison.Ordinal)
            && conversation.Contains("follow up", StringComparison.Ordinal)).IsTrue();
        provider.Release();
        var followedUpResult = await spawned.Wait(0, cancellationToken);
        _ = await Assert.That(followedUpResult.Output).IsEqualTo("followed up");
    }

    [Test]
    public async Task Send_delivers_a_child_message_to_its_parent(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "acknowledged", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)), _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        _ = await parent.SendTextMessage("initial", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await parent.Wait(0, cancellationToken);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var send = new AgentSendTool(child.Identity, Resolver(child, registry), child);

        var sent = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{parent.SessionId}}","message":"task completed"}"""),
            Turn(child, Router(provider)),
            cancellationToken)).Text;
        using var result = JsonDocument.Parse(sent);

        _ = await Assert.That(result.RootElement.GetProperty("session_id").GetString()).IsEqualTo(parent.SessionId);
        _ = await Assert.That(result.RootElement.GetProperty("status").GetString()).IsEqualTo("running");
        await provider.Arrived(cancellationToken);
        var conversation = string.Join('\n', provider.Requests[1].Messages.Select(message => message.Content));
        _ = await Assert.That(conversation).Contains("task completed");
        provider.Release();
        var completed = await parent.Wait(0, cancellationToken);

        _ = await Assert.That(completed.Output).IsEqualTo("acknowledged");
    }

    [Test]
    public async Task Send_delivers_a_child_message_to_its_literal_parent(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "acknowledged", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)), _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "parent-id", registry, cancellationToken);
        _ = await parent.SendTextMessage("initial", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await parent.Wait(0, cancellationToken);
        var child = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        var send = new AgentSendTool(child.Identity, Resolver(child, registry), child);

        var sent = (await send.Execute(
            new ToolInvocation(
                "test-call",
                """{"session_id":"parent","message":"task completed"}"""),
            Turn(child, Router(provider)),
            cancellationToken)).Text;
        using var result = JsonDocument.Parse(sent);

        _ = await Assert.That(result.RootElement.GetProperty("session_id").GetString()).IsEqualTo(parent.SessionId);
        _ = await Assert.That(result.RootElement.GetProperty("status").GetString()).IsEqualTo("running");
        await provider.Arrived(cancellationToken);
        var conversation = string.Join('\n', provider.Requests[1].Messages.Select(message => message.Content));
        _ = await Assert.That(conversation).Contains("task completed");
        provider.Release();
        var completed = await parent.Wait(0, cancellationToken);

        _ = await Assert.That(completed.Output).IsEqualTo("acknowledged");
    }

    [Test]
    public async Task Send_at_completion_boundary_is_delivered_once(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "second", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)), _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        var spawned = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        _ = await spawned.SendTextMessage("initial", cancellationToken);

        await provider.Arrived(cancellationToken);
        var sending = new AgentSendTool(parent.Identity, Resolver(parent, registry), parent).Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"boundary"}"""),
            Turn(parent, Router(provider)),
            cancellationToken);
        provider.Release();
        _ = await sending;
        await provider.Arrived(cancellationToken);

        var secondRequest = string.Join('\n', provider.Requests[1].Messages.Select(message => message.Content));
        _ = await Assert.That(secondRequest.Split("boundary", StringSplitOptions.None).Length - 1).IsEqualTo(1);
        provider.Release();
        var completed = await spawned.Wait(0, cancellationToken);
        _ = await Assert.That(completed.Output).IsEqualTo("second");
        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Send_accepts_a_message_at_the_32_kib_utf8_boundary(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "second", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)), _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var spawned = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        _ = await spawned.SendTextMessage("initial", cancellationToken);
        await provider.Arrived(cancellationToken);
        var boundary = new string('x', 32 * 1024);

        var sentJson = (await new AgentSendTool(parent.Identity, Resolver(parent, registry), parent).Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"{{boundary}}"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;

        using var sent = JsonDocument.Parse(sentJson);
        _ = await Assert.That(sent.RootElement.GetProperty("message_id").GetString()).StartsWith("msg-");
        provider.Release();
        await provider.Arrived(cancellationToken);
        _ = await Assert.That(provider.Requests[1].Messages.Select(message => message.Content)).Contains(boundary);
        provider.Release();
        var completed = await spawned.Wait(0, cancellationToken);
        _ = await Assert.That(completed.Output).IsEqualTo("second");
    }

    [Test]
    public async Task Send_validates_arguments_size_and_rejects_unrelated_agents(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)), _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var stranger = Session(provider, 0, "stranger", registry, cancellationToken);
        var spawned = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        _ = await spawned.SendTextMessage("initial", cancellationToken);
        var send = new AgentSendTool(parent.Identity, Resolver(parent, registry), parent);

        var malformed = (await send.Execute(new ToolInvocation("test-call", "{}"), Turn(parent, Router(provider)), cancellationToken)).Text;
        var blank = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":" "}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        var missing = (await send.Execute(
            new ToolInvocation(
                "test-call",
                """{"session_id":"missing","message":"hello"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        var invisible = (await new AgentSendTool(stranger.Identity, Resolver(stranger, registry), stranger).Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"hello"}"""),
            Turn(stranger, Router(provider)),
            cancellationToken)).Text;
        var oversized = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"{{new string('x', (32 * 1024) + 1)}}"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        var oversizedUnicode = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"{{new string('界', 10_923)}}"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;

        _ = await Assert.That(malformed).StartsWith("error:");
        _ = await Assert.That(blank).IsEqualTo("error: no message given");
        _ = await Assert.That(missing).IsEqualTo("error: child agent not found: missing");
        _ = await Assert.That(invisible).IsEqualTo("error: only parent/child may be sent");
        _ = await Assert.That(invisible).DoesNotContain(spawned.SessionId);
        _ = await Assert.That(oversized)
            .IsEqualTo("error: agent message exceeds 32768 UTF-8 bytes; split it into smaller messages");
        _ = await Assert.That(oversizedUnicode).IsEqualTo(oversized);
        provider.Release();
    }

    [Test]
    public async Task Spawn_rejects_a_foreground_profile(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)), _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var spawn = new AgentSpawnTool(TestModels.ScopeOf(parent), Router(provider));

        var result = (await spawn.Execute(new ToolInvocation("test-call", """{"prompt":"work","agent":"build"}"""), Turn(parent, Router(provider)), cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo("error: agent profile build cannot be spawned");
    }

    [Test]
    public async Task Spawn_rejects_a_profile_that_is_not_agent_selectable_even_when_user_selectable(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var profiles = TestModels.Profiles.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal);
        profiles["worker"] = profiles["worker"] with { IsUserSelectable = true, IsAgentSelectable = false };
        var profileRegistry = new ProfileRegistry(profiles, [], [], new HashSet<string>(StringComparer.Ordinal));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            profileRegistry,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var result = (await new AgentSpawnTool(TestModels.ScopeOf(parent), Router(provider)).Execute(
            new ToolInvocation("test-call", "{\"prompt\":\"work\",\"agent\":\"worker\"}"),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo("error: agent profile worker cannot be spawned");
    }

    [Test]
    public async Task Registry_enforces_session_wide_retained_limit_across_descendants(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.RegistryWithBudget(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            new RetainedAgentBudget(2),
            cancellationToken);
        await using var root = Session(provider, 0, "root", registry, cancellationToken);
        var first = TestModels.ScopeOf(root).AgentSpawner.SpawnScope(Request(root, "first")).Session;
        _ = TestModels.ScopeOf(first).AgentSpawner.SpawnScope(Request(first, "descendant")).Session;

        var rejected = await Assert.That(() => TestModels.ScopeOf(root).AgentSpawner.SpawnScope(Request(root, "third")).Session)
            .Throws<AgentRegistryException>();

        _ = await Assert.That(rejected?.Message).IsEqualTo("subagent retention limit reached");

        AgentLaunchRequest Request(IAgentSession parent, string name) => new(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Retiring_a_direct_child_releases_its_name_and_retained_budget(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.RegistryWithBudget(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1),
            cancellationToken);
        await using var root = Session(provider, 0, "root", registry, cancellationToken);
        var rootScope = TestModels.ScopeOf(root);
        var first = rootScope.AgentSpawner.SpawnScope(Request("worker"));

        var detached = rootScope.ChildRegistry.DetachDirectChildScope(first)
            ?? throw new InvalidOperationException("The registered child was not detached.");
        await detached.DisposeAsync();
        rootScope.AgentSpawner.ReleaseRetainedAgent(detached.Session.SessionId);
        var replacement = rootScope.AgentSpawner.SpawnScope(Request("worker"));

        _ = await Assert.That(rootScope.ChildRegistry.SnapshotDescendants()).HasSingleItem();
        _ = await Assert.That(replacement.Session.Name).IsEqualTo("worker");

        AgentLaunchRequest Request(string name) => new(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Registry_counts_a_pending_spawn_toward_the_session_wide_retained_limit(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        using var releaseConstruction = new ManualResetEventSlim();
        var sessions = new BlockingAgentSessions(
            new TestAgentSessions(Router(provider)),
            releaseConstruction);
        await using var registry = TestModels.RegistryWithBudget(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1),
            cancellationToken);
        await using var root = Session(provider, 0, "root", registry, cancellationToken);
        var spawning = Task.Run(() => TestModels.ScopeOf(root).AgentSpawner.SpawnScope(Request("pending")).Session, cancellationToken);
        await sessions.WaitUntilEntered(cancellationToken);

        var rejected = await Assert.That(() => TestModels.ScopeOf(root).AgentSpawner.SpawnScope(Request("rejected")).Session)
            .Throws<AgentRegistryException>();
        releaseConstruction.Set();
        _ = await spawning;

        _ = await Assert.That(rejected?.Message).IsEqualTo("subagent retention limit reached");

        AgentLaunchRequest Request(string name) => new(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Registry_counts_live_security_ancestry(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        using var releaseConstruction = new ManualResetEventSlim();
        var configuredProfiles = TestModels.Profiles.ToDictionary(
            entry => entry.Key,
            entry => entry.Value,
            StringComparer.Ordinal);
        configuredProfiles["worker"] = configuredProfiles["worker"] with { RecursionLimit = 1 };
        var profiles = new ProfileRegistry(configuredProfiles, [], [], new HashSet<string>(StringComparer.Ordinal));
        var sessions = new BlockingAgentSessions(
            new TestAgentSessions(Router(provider)),
            releaseConstruction);
        await using var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            profiles,
            TestModels.PromptTemplates,
            cancellationToken);
        await using var root = Session(provider, 0, "root", registry, cancellationToken);
        var spawning = Task.Run(() => TestModels.ScopeOf(root).AgentSpawner.SpawnScope(Request("pending")).Session, cancellationToken);
        await sessions.WaitUntilEntered(cancellationToken);

        releaseConstruction.Set();
        var child = await spawning;
        root.UpdateSelection(root.CurrentSelection().RequestedModel, Profile("root", readOnly: true, []));

        _ = await Assert.That(child.ResolvePolicySelection().SecurityProfile.ReadOnly).IsTrue();
        var childRecursion = await Assert.That(() => TestModels.ScopeOf(child).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            child,
            Turn(child, Router(provider)),
            "worker",
            child.CurrentSelection().RequestedModel,
            "descendant",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session)
            .Throws<AgentRegistryException>();
        _ = await Assert.That(childRecursion?.Message)
            .IsEqualTo("subagent profile recursion limit reached");

        AgentLaunchRequest Request(string name) => new(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Registry_rolls_back_global_admission_when_creation_fails(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.RegistryWithBudget(
            new UnsupportedAgentSessionFactory(),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            new RetainedAgentBudget(1),
            cancellationToken);
        await using var root = Session(provider, 0, "root", registry, cancellationToken);

        _ = await Assert.That(() => TestModels.ScopeOf(root).AgentSpawner.SpawnScope(Request("first")).Session)
            .Throws<NotSupportedException>();
        _ = await Assert.That(() => TestModels.ScopeOf(root).AgentSpawner.SpawnScope(Request("second")).Session)
            .Throws<NotSupportedException>();

        AgentLaunchRequest Request(string name) => new(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.CurrentSelection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Registry_enforces_depth_limit(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "idle", []),
            LLMEvent.Completed("stop", 1, 0, 1, "one", []),
            LLMEvent.Completed("stop", 1, 0, 1, "two", []),
            LLMEvent.Completed("stop", 1, 0, 1, "three", []),
            LLMEvent.Completed("stop", 1, 0, 1, "four", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)), _broker, _repository, TestModels.ProfileRegistry(), TestModels.PromptTemplates, cancellationToken);
        await using var parent = Session(provider, 0, "parent", registry, cancellationToken);
        var spawn = new AgentSpawnTool(TestModels.ScopeOf(parent), Router(provider));
        var idle = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "idle",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        _ = await idle.SendTextMessage("become idle", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await idle.Wait(0, cancellationToken);

        var deepParent = Session(provider, 4, "deep-parent", registry, cancellationToken);
        var tooDeep = (await new AgentSpawnTool(TestModels.ScopeOf(deepParent), Router(provider)).Execute(
            new ToolInvocation(
                "test-call",
                """{"prompt":"too deep","agent":"worker"}"""),
            Turn(deepParent, Router(provider)),
            cancellationToken)).Text;

        _ = await Assert.That(tooDeep).IsEqualTo("error: subagent depth limit reached");
    }

    [Test]
    public async Task Disposing_the_registry_cancels_and_joins_an_active_follow_up(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "unreachable", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "agent", registry, cancellationToken);
        var spawned = TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session;
        _ = await spawned.SendTextMessage("first", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await spawned.Wait(0, cancellationToken);
        var send = new AgentSendTool(parent.Identity, Resolver(parent, registry), parent);
        _ = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"wait forever"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        await provider.Arrived(cancellationToken);

        await registry.DisposeAsync();
        var terminal = await spawned.Wait(0, cancellationToken);

        _ = await Assert.That(terminal.Status).IsEqualTo(AgentTaskStatus.Canceled);
        _ = await Assert.That(terminal.Error).IsEqualTo("interrupted");
        var failed = _repository.Replay().Single(
            published => published.PayloadCase == Event.PayloadOneofCase.AgentFailed);
        _ = await Assert.That(failed.AgentSessionId).IsEqualTo(spawned.SessionId);
        _ = await Assert.That(failed.AgentFailed.ParentAgentSessionId).IsEqualTo("agent");
        _ = await Assert.That(failed.AgentFailed.Name).IsEqualTo("worker");
        _ = await Assert.That(failed.AgentFailed.Message).IsEqualTo("interrupted");
        _ = await Assert.That(() => TestModels.ScopeOf(parent).AgentSpawner.SpawnScope(new AgentLaunchRequest(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "worker",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly)).Session)
            .Throws<AgentRegistryException>();
        var rejected = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"again"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        _ = await Assert.That(rejected).IsEqualTo("error: the user session is shutting down");
    }

    [Test]
    public async Task Shutdown_waits_for_child_construction_without_disposing_the_borrowed_parent_scope(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        using var releaseConstruction = new ManualResetEventSlim();
        var sessions = new BlockingAgentSessions(
            new TestAgentSessions(Router(provider)),
            releaseConstruction);
        await using var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var identity = AgentIdentity.Main("parent", string.Empty, TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        await using var parentScope = TestAgentSessionScope.Build(
            identity,
            AgentSessionParentLink.Root(),
            registry,
            TestModels.PromptTemplates,
            (sessionParentScope, _, children, childQuestions) => new AgentSession(identity, sessionParentScope, new ModelSelector("stepped/model"), Router(provider), _broker, _repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(), new ContextCadence(), TestModels.PromptTemplates, childQuestions, dependencies.ExitReminder, dependencies.Profile, TestModels.CompletionCallbacks(childQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, _repository, _broker), SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), dependencies.Status, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), cancellationToken));
        var parent = parentScope.Session;
        registry.RegisterRootScope(parentScope);
        TestModels.RegisterScope(parentScope);
        var spawning = Task.Run(() => parentScope.AgentSpawner.SpawnScope(Request()).Session, cancellationToken);
        await sessions.WaitUntilEntered(cancellationToken);

        var shutdown = registry.DisposeAsync().AsTask();

        _ = await Assert.That(shutdown.IsCompleted).IsFalse();
        releaseConstruction.Set();
        AgentRegistryException? spawnFailure = null;
        try
        {
            _ = await spawning.ConfigureAwait(false);
        }
        catch (AgentRegistryException exception)
        {
            spawnFailure = exception;
        }

        _ = await Assert.That(spawnFailure).IsNotNull();
        await shutdown;
        _ = await Assert.That(sessions.CreatedScope).IsNotNull();
        var createdScope = sessions.CreatedScope ?? throw new InvalidOperationException("Expected a constructed child scope.");
        _ = await Assert.That(registry.ContainsScope(createdScope)).IsFalse();
        TestModels.UnregisterScope(parentScope);

        AgentLaunchRequest Request() => new(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "blocked",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Failed_duplicate_root_registration_does_not_register_the_rejected_scope(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider)),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        _ = Session(provider, 0, "duplicate", registry, cancellationToken);
        var registeredScope = _rootScopes[^1];
        var identity = AgentIdentity.Main("duplicate", string.Empty, TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        InvalidOperationException? unattachedAccess = null;
        var candidate = TestAgentSessionScope.Build(
            identity,
            AgentSessionParentLink.Root(),
            registry,
            TestModels.PromptTemplates,
            (sessionParentScope, owningScope, children, childQuestions) =>
            {
                try
                {
                    _ = owningScope.Session;
                }
                catch (InvalidOperationException exception)
                {
                    unattachedAccess = exception;
                }

                return new AgentSession(identity, sessionParentScope, new ModelSelector("stepped/model"), Router(provider), _broker, _repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(), new ContextCadence(), TestModels.PromptTemplates, childQuestions, dependencies.ExitReminder, dependencies.Profile, TestModels.CompletionCallbacks(childQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, _repository, _broker), SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), dependencies.Status, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), cancellationToken);
            });
        await using var candidateScope = candidate;

        _ = await Assert.That(unattachedAccess?.Message)
            .IsEqualTo("The agent session is not attached to its scope.");
        var duplicateAttachment = await Assert.That(() => candidate.AttachSession(candidate.Session))
            .Throws<InvalidOperationException>();
        _ = await Assert.That(duplicateAttachment?.Message)
            .IsEqualTo("The agent session is already attached to its scope.");
        _ = await Assert.That(() => registry.RegisterRootScope(candidate))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(registry.ContainsScope(candidate)).IsFalse();
        registry.UnregisterRootScope(registeredScope);
        registry.RegisterRootScope(candidate);

        registry.UnregisterRootScope(candidate);
    }

    [Test]
    public async Task Child_registries_are_identity_bound_and_distinct_per_scope(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", registry, cancellationToken);
        var firstScope = _rootScopes[^1];
        var secondParent = Session(provider, 0, "second-parent", registry, cancellationToken);
        var secondScope = _rootScopes[^1];

        _ = await Assert.That(ReferenceEquals(firstScope.ChildRegistry, secondScope.ChildRegistry)).IsFalse();
        firstScope.ParentScope.ValidateOwnerScope(firstScope);
        secondScope.ParentScope.ValidateOwnerScope(secondScope);
        var mismatch = await Assert.That(() => firstScope.AgentSpawner.SpawnScope(Request(secondParent)).Session)
            .Throws<AgentRegistryException>();
        var child = firstScope.AgentSpawner.SpawnScope(Request(firstParent)).Session;

        _ = await Assert.That(mismatch?.Message)
            .IsEqualTo("launch parent does not match child registry owner: expected first-parent, actual second-parent");
        _ = await Assert.That(child.ParentSessionId).IsEqualTo(firstParent.SessionId);
        _ = await Assert.That(sessions.ParentScopes.Single().Parent).IsSameReferenceAs(firstScope);
        _ = await Assert.That(ReferenceEquals(firstScope.ChildRegistry, sessions.Scopes.Single().ChildRegistry)).IsFalse();

        AgentLaunchRequest Request(IAgentSession parent) => new(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            "child",
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Spawn_requires_the_registered_parent_instance_and_passes_its_exact_scope(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var registeredParent = Session(provider, 0, "registered-parent", registry, cancellationToken);
        var registeredParentScope = _rootScopes[^1];
        var otherRegistry = TestModels.Registry(
            new UnsupportedAgentSessionFactory(),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        var unregisteredParent = Session(
            provider,
            0,
            "unregistered-parent",
            otherRegistry,
            cancellationToken);
        var mismatchedParent = Session(
            provider,
            0,
            registeredParent.SessionId,
            otherRegistry,
            cancellationToken);

        var missing = await Assert.That(() => TestModels.ScopeOf(registeredParent).AgentSpawner.SpawnScope(Request(unregisteredParent, "missing")).Session)
            .Throws<AgentRegistryException>();
        var mismatched = await Assert.That(() => TestModels.ScopeOf(registeredParent).AgentSpawner.SpawnScope(Request(mismatchedParent, "mismatched")).Session)
            .Throws<AgentRegistryException>();
        var child = TestModels.ScopeOf(registeredParent).AgentSpawner.SpawnScope(Request(registeredParent, "captured")).Session;

        _ = await Assert.That(missing?.Message)
            .IsEqualTo("launch parent does not match child registry owner: expected registered-parent, actual unregistered-parent");
        _ = await Assert.That(mismatched?.Message)
            .IsEqualTo("launch parent does not match child registry owner: expected registered-parent, actual registered-parent");
        _ = await Assert.That(sessions.ParentScopes).HasSingleItem();
        _ = await Assert.That(sessions.ParentScopes.Single().Parent).IsSameReferenceAs(registeredParentScope);
        _ = await Assert.That(child.Name).IsEqualTo("captured");

        AgentLaunchRequest Request(IAgentSession parent, string name) => new(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.CurrentSelection().RequestedModel,
            name,
            string.Empty,
            HistoryForkSelection.Parse(string.Empty),
            new HistoryForkBoundary.AfterCompletedHistory(),
            AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    [Test]
    public async Task Child_registry_rejects_duplicate_names_without_removing_the_original(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var sessions = new TestAgentSessions(Router(provider));
        await using var registry = TestModels.Registry(
            sessions,
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            TestModels.PromptTemplates,
            cancellationToken);
        await using var parent = Session(provider, 0, "parent", "parent", registry, cancellationToken);
        var parentScope = TestModels.ScopeOf(parent);
        var mode = Profile("worker", false, []);
        var first = sessions.Create(
            ChildIdentity("first-child"),
            AgentSessionParentLink.Child(parentScope, AgentCompletionDeliveryPolicy.RetainedOnly),
            new ModelSelector("stepped/model"),
            _broker,
            _repository,
            mode,
            mode.SecurityProfile,
            registry.RequireStatus(),
            registry,
            cancellationToken);
        var second = sessions.Create(
            ChildIdentity("second-child"),
            AgentSessionParentLink.Child(parentScope, AgentCompletionDeliveryPolicy.RetainedOnly),
            new ModelSelector("stepped/model"),
            _broker,
            _repository,
            mode,
            mode.SecurityProfile,
            registry.RequireStatus(),
            registry,
            cancellationToken);
        await using var rejected = second;

        _ = await Assert.That(parentScope.ChildRegistry.TryAdd(first)).IsTrue();
        _ = await Assert.That(() => parentScope.ChildRegistry.TryAdd(second)).Throws<ChildNameConflictException>();
        _ = await Assert.That(parentScope.ChildRegistry.ResolveDirectChildScope(first.Session.SessionId))
            .IsSameReferenceAs(first);
        _ = await Assert.That(parentScope.ChildRegistry.ResolveNamedChildScope("duplicate"))
            .IsSameReferenceAs(first);
        _ = await Assert.That(parentScope.ChildRegistry.FindDirectChildScope(second.Session.SessionId)).IsNull();
        _ = await Assert.That(parentScope.ChildRegistry.SnapshotDescendants()).HasSingleItem()
            .And.Contains(first.Session);

        AgentIdentity ChildIdentity(string sessionId) => AgentIdentity.Child(
            sessionId,
            parent.SessionId,
            parent.Name,
            "duplicate",
            1,
            AgentScope.Empty(TestModels.PromptTemplates),
            TestModels.PromptTemplates);
    }

    private static NoopMode Profile(
        string id,
        bool readOnly,
        IReadOnlyList<SandboxRule> runtimeCapabilities)
    {
        var profile = new AgentProfile(
            id,
            new ProfileConfig("Test prompt", "Test profile.", null, 1, 3, readOnly, true, false, true, []),
            [],
            [],
            new HashSet<string>(StringComparer.Ordinal));
        return new NoopMode(profile, SecurityProfile.Compose(readOnly, [], [], runtimeCapabilities));
    }

    private static AgentResolver Resolver(IAgentSession session, AgentRegistry registry) =>
        new(session.Identity, ParentScope(session, registry), TestModels.ScopeOf(session), registry);

    private static AgentSessionParentScope ParentScope(IAgentSession session, AgentRegistry registry)
    {
        if (session.ParentSessionId.Length == 0)
        {
            return AgentSessionParentScope.Root();
        }

        var parent = registry.FindScope(session.ParentSessionId)
            ?? throw new AgentRegistryException($"parent agent not found: {session.ParentSessionId}");
        return AgentSessionParentScope.Child(parent, AgentCompletionDeliveryPolicy.RetainedOnly);
    }

    private static AgentTurnSelection Turn(IAgentSession session, ModelRouter router)
    {
        var selection = session.CurrentSelection();
        return new AgentTurnSelection(
            selection.RequestedModel,
            router.Resolve(selection.RequestedModel.Value),
            selection.Profile,
            selection.SecurityProfile);
    }

    private static ModelRouter Router(ILLMProvider provider) =>
        Router(provider, []);

    private static ModelRouter Router(
        ILLMProvider provider,
        IReadOnlyList<ModelAliasDefinition> aliases)
    {
        var models = new[] { new LLMModel("model", provider.Id), new LLMModel("replacement", provider.Id) };
        var registry = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>> { [provider.Id] = models });
        return new ModelRouter(registry, new ModelRouting(new ModelAliasCatalog(registry, aliases), "stepped/model"));
    }

    private IAgentSession Session(
        ILLMProvider provider,
        int depth,
        string sessionId,
        AgentRegistry registry,
        CancellationToken cancellationToken) =>
        Session(provider, depth, sessionId, depth == 0 ? string.Empty : "parent", registry, cancellationToken);

    private IAgentSession Session(
        ILLMProvider provider,
        int depth,
        string sessionId,
        string name,
        AgentRegistry registry,
        CancellationToken cancellationToken)
    {
        var router = Router(provider);
        var parentLink = AgentSessionParentLink.Root();
        if (depth > 0)
        {
            var ancestor = Session(provider, 0, "ancestor", "ancestor-agent", registry, cancellationToken);
            parentLink = AgentSessionParentLink.Child(TestModels.ScopeOf(ancestor), AgentCompletionDeliveryPolicy.RetainedOnly);
        }

        var identity = depth == 0
            ? AgentIdentity.Main(sessionId, name, TestModels.PromptTemplates)
            : AgentIdentity.Child(sessionId, "ancestor", "ancestor-agent", name, depth, AgentScope.Empty(TestModels.PromptTemplates), TestModels.PromptTemplates);
        using var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        using var scope = TestAgentSessionScope.Build(identity, parentLink, registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) =>
            new AgentSession(identity, sessionParentScope, new ModelSelector($"{provider.Id}/model"), router, _broker, _repository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(), new ContextCadence(), TestModels.PromptTemplates, childQuestions, dependencies.ExitReminder, dependencies.Profile, TestModels.CompletionCallbacks(childQuestions, dependencies.ActiveWorkReminder, dependencies.ExitReminder, _repository, _broker), SecurityProfileTestFactory.Create(SecurityProfile.Compose(readOnly: false, [], [], [])), dependencies.Status, dependencies.Queues, new AgentSessionActivity(TimeProvider.System), cancellationToken));
        TestModels.RegisterScope(scope);
        if (depth == 0)
        {
            registry.RegisterRootScope(scope);
            _rootScopes.Add(scope);
        }

        return scope.Session;
    }

    private sealed class BlockingAgentSessions(IAgentSessionFactory sessions, ManualResetEventSlim release) : IAgentSessionFactory
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IAgentSessionScope? CreatedScope { get; private set; }

        public Task WaitUntilEntered(CancellationToken cancellationToken) => _entered.Task.WaitAsync(cancellationToken);

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
            _ = _entered.TrySetResult();
            release.Wait(CancellationToken.None);
            CreatedScope = sessions.Create(
                identity,
                parentLink,
                model,
                eventBroker,
                eventRepository,
                mode,
                securityProfile,
                status,
                registry,
                CancellationToken.None);
            return CreatedScope;
        }
    }

    private sealed class UnsupportedAgentSessionFactory : IAgentSessionFactory
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
            CancellationToken lifetime) =>
            throw new NotSupportedException("This test session does not support spawning subagents.");
    }

    private sealed class TestAgentSessions(ModelRouter router) : IAgentSessionFactory
    {
        private static readonly ConcurrentBag<UserSessionResources> Resources = [];
        private static readonly ConcurrentBag<ShellProcessOwners> ProcessOwners = [];
        private static readonly ConcurrentBag<AgentQueueCatalog> QueueCatalogs = [];
        private readonly List<AgentIdentity> _identities = [];
        private readonly List<ModelSelector> _models = [];

        public IReadOnlyList<AgentIdentity> Identities => _identities;

        public IReadOnlyList<ModelSelector> Models => _models;

        public List<IMode> Profiles { get; } = [];

        public List<SecurityProfile> SecurityProfiles { get; } = [];

        public List<IAgentSessionScope> Scopes { get; } = [];

        public List<AgentSessionParentScope> ParentScopes { get; } = [];

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
            _identities.Add(identity);
            _models.Add(model);
            Profiles.Add(mode);
            SecurityProfiles.Add(securityProfile);
            var root = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("N"))).FullName;
            var resources = new UserSessionResources(
                new StatePaths(root, root, root),
                UserSessionId.Parse(Guid.NewGuid().ToString("N")),
                ProjectWorkspace.FromLaunchDirectory(root));
            var processes = new ShellProcessOwners(resources, new ProcessRunner(string.Empty), lifetime);
            var queueCatalog = new AgentQueueCatalog(resources);
            var processOwner = processes.Prepare(identity.SessionId);
            processes.Register(processOwner);
            if (identity.ParentSessionId.Length > 0)
            {
                _ = queueCatalog.Register(AgentIdentity.Main(identity.ParentSessionId, identity.ParentSessionName, TestModels.PromptTemplates));
            }

            var queues = queueCatalog.Register(identity);
            Resources.Add(resources);
            ProcessOwners.Add(processes);
            QueueCatalogs.Add(queueCatalog);

            var scope = TestAgentSessionScope.Build(identity, parentLink, registry, TestModels.PromptTemplates, (sessionParentScope, _, children, childQuestions) =>
            {
                var exitReminder = new ExitReminder(eventRepository, TestModels.PromptTemplates, identity.SessionId);
                var session = new AgentSession(identity, sessionParentScope, model, router, eventBroker, eventRepository, [], TestModels.EmptyToolDefinitions, TestModels.MaterializePrompt(identity, ".", "."), new ToolOutputBlobStore(Path.GetTempPath()), TestModels.CompactionGroupBlobs(), new Compactor(90, 30, 60_000, 1024, TestModels.PromptTemplates), new ProviderSessions(), new ContextCadence(), TestModels.PromptTemplates, childQuestions, exitReminder, mode, TestModels.CompletionCallbacks(childQuestions, new ActiveWorkCompletionReminder(children, processOwner, TestModels.PromptTemplates, null), exitReminder, eventRepository, eventBroker), SecurityProfileTestFactory.Create(securityProfile), new RuntimeStatus(queueCatalog, processes, registry, TestModels.PromptTemplates, TimeProvider.System), queues, new AgentSessionActivity(TimeProvider.System), lifetime);
                queues.Attach(session);
                return session;
            });
            Scopes.Add(scope);
            if (parentLink.Parent is not null)
            {
                ParentScopes.Add(scope.ParentScope);
            }

            TestModels.RegisterScope(scope);
            return scope;
        }
    }
}

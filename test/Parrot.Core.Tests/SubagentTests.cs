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

internal sealed class SubagentTests : IDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();
    private readonly EventRepository _repository;

    public SubagentTests() => _repository = new EventRepository(_database);

    public void Dispose()
    {
        _broker.Dispose();
        _database.Dispose();
    }

    [Test]
    public async Task Spawn_returns_immediately_and_wait_retains_the_terminal_result(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "child says hi", []));
        var sessions = new TestAgentSessions(Router(provider), deliversCompletions: false);
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawn = new AgentSpawnTool(registry, Router(provider), parent);
        var wait = new WaitAgentTool(registry, parent);

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

        var yieldedJson = (await wait.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{sessionId}}","yield_after_ms":1}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        using var yielded = JsonDocument.Parse(yieldedJson);
        _ = await Assert.That(yielded.RootElement.GetProperty("yielded").GetBoolean()).IsTrue();
        _ = await Assert.That(yielded.RootElement.GetProperty("status").GetString()).IsEqualTo("running");

        provider.Release();
        var completedJson = (await wait.Execute(
            new ToolInvocation(
                "test-call",
                """{"session_id":"child-helper"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        using var completed = JsonDocument.Parse(completedJson);
        var retainedJson = (await wait.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{sessionId}}"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        using var retained = JsonDocument.Parse(retainedJson);

        _ = await Assert.That(completed.RootElement.GetProperty("task_id").GetString()).IsEqualTo(sessionId);
        _ = await Assert.That(completed.RootElement.GetProperty("status").GetString()).IsEqualTo("succeeded");
        _ = await Assert.That(completed.RootElement.GetProperty("elapsed_ms").GetInt64() >= 0).IsTrue();
        _ = await Assert.That(completed.RootElement.GetProperty("output").GetString()).IsEqualTo("child says hi");
        _ = await Assert.That(retained.RootElement.GetProperty("output").GetString()).IsEqualTo("child says hi");
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
    public async Task Friendly_names_are_scoped_to_direct_siblings(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", cancellationToken);
        var first = registry.Spawn(
            firstParent,
            Turn(firstParent, Router(provider)),
            "worker",
            firstParent.Selection().RequestedModel,
            "helper",
            string.Empty);
        var sibling = registry.Spawn(
            firstParent,
            Turn(firstParent, Router(provider)),
            "worker",
            firstParent.Selection().RequestedModel,
            "helper",
            string.Empty);
        var otherBranch = registry.Spawn(
            secondParent,
            Turn(secondParent, Router(provider)),
            "worker",
            secondParent.Selection().RequestedModel,
            "helper",
            string.Empty);

        _ = await Assert.That(first.Name).IsEqualTo("helper");
        _ = await Assert.That(sibling.Name).IsEqualTo("helper-2");
        _ = await Assert.That(otherBranch.Name).IsEqualTo("helper");
        _ = await Assert.That(registry.GetChild(firstParent, "helper")).IsSameReferenceAs(first);
        _ = await Assert.That(registry.GetChild(secondParent, "helper")).IsSameReferenceAs(otherBranch);
    }

    [Test]
    public async Task Scope_changes_inherit_through_the_registry_and_use_canonical_names(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var root = Session(provider, 0, "root", "root-agent", cancellationToken);
        var planner = registry.Spawn(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.Selection().RequestedModel,
            "  Planner!  ",
            "Plan the migration.");
        var worker = registry.Spawn(
            planner,
            Turn(planner, Router(provider)),
            "worker",
            planner.Selection().RequestedModel,
            "worker",
            string.Empty);
        var reviewer = registry.Spawn(
            worker,
            Turn(worker, Router(provider)),
            "worker",
            worker.Selection().RequestedModel,
            "reviewer",
            "Plan the migration.");
        var implementer = registry.Spawn(
            worker,
            Turn(worker, Router(provider)),
            "worker",
            worker.Selection().RequestedModel,
            "implementer",
            "Implement the migration.");

        _ = await Assert.That(planner.Name).IsEqualTo("planner");
        _ = await Assert.That(worker.ResolveScope().Format(worker.Depth)).IsEqualTo(
            "## Scope\n\n"
            + "### 1st Ancestor (planner)\n"
            + "Plan the migration.");
        _ = await Assert.That(reviewer.ResolveScope().Format(reviewer.Depth)).IsEqualTo(
            "## Scope\n\n"
            + "### 2nd Ancestor (planner)\n"
            + "Plan the migration.");
        _ = await Assert.That(implementer.ResolveScope().Format(implementer.Depth)).IsEqualTo(
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
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", cancellationToken);
        var target = registry.Spawn(
            firstParent,
            Turn(firstParent, Router(provider)),
            "worker",
            firstParent.Selection().RequestedModel,
            "helper",
            string.Empty);

        _ = await Assert.That(registry.GetChild(secondParent, target.SessionId)).IsSameReferenceAs(target);
        var unrelated = await Assert.That(() => registry.GetRecipient(secondParent, target.SessionId))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(unrelated?.Message).IsEqualTo("only parent/child may be sent");
        _ = await Assert.That(() => registry.GetChild(secondParent, "helper")).Throws<AgentRegistryException>();
        _ = await Assert.That(() => registry.GetRecipient(secondParent, "helper")).Throws<AgentRegistryException>();
    }

    [Test]
    public async Task Send_resolution_prefers_canonical_ids_then_the_parent_name(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var root = Session(provider, 0, "root-id", cancellationToken);
        var parent = registry.Spawn(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.Selection().RequestedModel,
            "parent-name",
            string.Empty);
        var caller = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "caller",
            string.Empty);
        var parentNameCollision = registry.Spawn(
            caller,
            Turn(caller, Router(provider)),
            "worker",
            caller.Selection().RequestedModel,
            parent.Name,
            string.Empty);
        var canonicalCollision = registry.Spawn(
            caller,
            Turn(caller, Router(provider)),
            "worker",
            caller.Selection().RequestedModel,
            parentNameCollision.SessionId,
            string.Empty);

        _ = await Assert.That(registry.GetRecipient(caller, parent.Name)).IsSameReferenceAs(parent);
        _ = await Assert.That(registry.GetChild(caller, parent.Name)).IsSameReferenceAs(parentNameCollision);
        _ = await Assert.That(canonicalCollision.Name).IsEqualTo(parentNameCollision.SessionId);
        _ = await Assert.That(registry.GetRecipient(caller, canonicalCollision.Name))
            .IsSameReferenceAs(parentNameCollision);
    }

    [Test]
    public async Task Send_rejects_a_spawned_grandparent_without_exposing_its_id(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var root = Session(provider, 0, "root-id", cancellationToken);
        var grandparent = registry.Spawn(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.Selection().RequestedModel,
            "grandparent",
            string.Empty);
        var parent = registry.Spawn(
            grandparent,
            Turn(grandparent, Router(provider)),
            "worker",
            grandparent.Selection().RequestedModel,
            "parent-agent",
            string.Empty);
        var sender = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "sender",
            string.Empty);
        var send = new AgentSendTool(registry, sender);

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
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var firstParent = Session(provider, 0, "first-parent", cancellationToken);
        var secondParent = Session(provider, 0, "second-parent", cancellationToken);
        var first = registry.Spawn(
            firstParent,
            Turn(firstParent, Router(provider)),
            "worker",
            firstParent.Selection().RequestedModel,
            string.Empty,
            string.Empty);
        var second = registry.Spawn(
            secondParent,
            Turn(secondParent, Router(provider)),
            "worker",
            secondParent.Selection().RequestedModel,
            first.Name,
            string.Empty);

        _ = await Assert.That(second.Name).IsEqualTo(first.Name);
        _ = await Assert.That(registry.GetChild(firstParent, first.Name)).IsSameReferenceAs(first);
        _ = await Assert.That(registry.GetChild(secondParent, first.Name)).IsSameReferenceAs(second);
    }

    [Test]
    public async Task Send_resolves_literal_parent_before_a_direct_child_name_collision(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var root = Session(provider, 0, "root-id", "root", cancellationToken);
        var parent = registry.Spawn(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.Selection().RequestedModel,
            "actual-parent",
            string.Empty);
        var caller = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "caller",
            string.Empty);
        var namedParent = registry.Spawn(
            caller,
            Turn(caller, Router(provider)),
            "worker",
            caller.Selection().RequestedModel,
            "parent",
            string.Empty);

        _ = await Assert.That(registry.GetRecipient(caller, "parent")).IsSameReferenceAs(parent);
        _ = await Assert.That(registry.GetRecipient(caller, parent.SessionId)).IsSameReferenceAs(parent);
        _ = await Assert.That(registry.GetRecipient(caller, parent.Name)).IsSameReferenceAs(parent);
        _ = await Assert.That(registry.GetChild(caller, "parent")).IsSameReferenceAs(namedParent);
        _ = await Assert.That(registry.GetChild(caller, namedParent.SessionId)).IsSameReferenceAs(namedParent);
    }

    [Test]
    public async Task Root_resolves_a_child_named_parent_and_otherwise_reports_not_found(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var root = Session(provider, 0, "root-id", "root", cancellationToken);
        var emptyRoot = Session(provider, 0, "empty-root-id", "empty-root", cancellationToken);
        var child = registry.Spawn(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.Selection().RequestedModel,
            "parent",
            string.Empty);

        _ = await Assert.That(registry.GetRecipient(root, "parent")).IsSameReferenceAs(child);
        _ = await Assert.That(registry.GetChild(root, "parent")).IsSameReferenceAs(child);
        var missing = await Assert.That(() => registry.GetRecipient(emptyRoot, "parent"))
            .Throws<AgentRegistryException>();
        _ = await Assert.That(missing?.Message).IsEqualTo("child agent not found: parent");
    }

    [Test]
    public async Task Send_metadata_describes_literal_parent_and_accepted_recipient_forms(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var session = Session(provider, 0, "root-id", cancellationToken);
        var send = new AgentSendTool(registry, session);
        using var schema = JsonDocument.Parse(send.ParametersJson);
        var sessionIdDescription = schema.RootElement.GetProperty("properties").GetProperty("session_id")
            .GetProperty("description").GetString();

        _ = await Assert.That(send.Description).Contains("direct parent or direct child");
        _ = await Assert.That(send.Description).Contains("literal 'parent'");
        _ = await Assert.That(send.Description).Contains("Exact canonical session IDs for those recipients");
        _ = await Assert.That(send.Description).Contains("Direct-child friendly names");
        _ = await Assert.That(sessionIdDescription).Contains("direct parent or direct child");
        _ = await Assert.That(sessionIdDescription).Contains("literal 'parent'");
        _ = await Assert.That(sessionIdDescription).Contains("direct-child friendly name");
    }

    [Test]
    public async Task Completion_automatically_starts_a_parent_follow_up(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "child result", []),
            LLMEvent.Completed("stop", 1, 0, 1, "parent result", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: true),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var parent = Session(provider, 0, "parent", cancellationToken);
        var child = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "helper",
            string.Empty);

        _ = await child.Send("do work", cancellationToken);
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
        _ = await parent.ResultSettled();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(2);
    }

    [Test]
    public async Task Nested_completion_starts_an_idle_parent_execution_and_notifies_the_root(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "intermediate ready", []),
            LLMEvent.Completed("stop", 1, 0, 1, "root initial result", []),
            LLMEvent.Completed("stop", 1, 0, 1, "nested result", []),
            LLMEvent.Completed("stop", 1, 0, 1, "intermediate result", []),
            LLMEvent.Completed("stop", 1, 0, 1, "root result", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: true),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var root = Session(provider, 0, "root", cancellationToken);
        var intermediate = registry.Spawn(
            root,
            Turn(root, Router(provider)),
            "worker",
            root.Selection().RequestedModel,
            "intermediate",
            string.Empty);
        _ = await intermediate.Send("prepare", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await root.ResultSettled();
        var nested = registry.Spawn(
            intermediate,
            Turn(intermediate, Router(provider)),
            "worker",
            intermediate.Selection().RequestedModel,
            "nested",
            string.Empty);

        _ = await nested.Send("inspect", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        await provider.Arrived(cancellationToken);
        var intermediateNotification = string.Join('\n', provider.Requests[3].Messages.Select(message => message.Content));
        _ = await Assert.That(intermediateNotification).Contains($"Child agent session: {nested.SessionId}");
        provider.Release();
        await provider.Arrived(cancellationToken);
        var rootNotification = string.Join('\n', provider.Requests[4].Messages.Select(message => message.Content));
        _ = await Assert.That(rootNotification).Contains($"Child agent session: {intermediate.SessionId}");
        _ = await Assert.That(rootNotification).Contains("intermediate result");
        provider.Release();
        _ = await root.ResultSettled();

        _ = await Assert.That(provider.Requests).Count().IsEqualTo(5);
    }

    [Test]
    public async Task Completion_bounds_a_multibyte_terminal_error(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await Task.CompletedTask;
        var identity = AgentIdentity.Child("child", "parent", "parent", "helper", 1, AgentScope.Empty);
        var notification = AgentExecution.Failed(new string('界', 262_144)).FormatCompletion(identity);

        _ = await Assert.That(notification).Contains("Status: failed");
        _ = await Assert.That(notification).Contains("Error:");
        _ = await Assert.That(System.Text.Encoding.UTF8.GetByteCount(notification) <= 1024 * 1024).IsTrue();
    }

    [Test]
    public async Task Spawn_resolves_the_explore_alias_to_the_canonical_child_profile(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var sessions = new TestAgentSessions(Router(provider), deliversCompletions: false);
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawn = new AgentSpawnTool(registry, Router(provider), parent);

        _ = (await spawn.Execute(new ToolInvocation("test-call", """{"prompt":"inspect","agent":"explore"}"""), Turn(parent, Router(provider)), cancellationToken)).Text;
        await provider.Arrived(cancellationToken);
        provider.Release();

        _ = await Assert.That(sessions.Profiles.Single()?.Id).IsEqualTo("explorer");
    }

    [Test]
    public async Task Spawn_uses_the_selection_captured_before_parent_selection_changes(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var sessions = new TestAgentSessions(Router(provider), deliversCompletions: false);
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        parent.UpdateSelection(parent.Selection().RequestedModel, Profile(ModeRegistry.Build, readOnly: false, []));
        var spawn = new AgentSpawnTool(registry, Router(provider), parent);
        var capturedSelection = Turn(parent, Router(provider));
        parent.UpdateSelection(
            new ModelSelector("stepped/replacement"),
            profile: null);

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
        var sessions = new TestAgentSessions(router, deliversCompletions: false);
        await using var registry = TestModels.Registry(sessions, _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        parent.UpdateSelection(new ModelSelector("fast"), profile: null);
        var spawn = new AgentSpawnTool(registry, router, parent);

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
        var sessions = new TestAgentSessions(router, deliversCompletions: false);
        await using var registry = TestModels.Registry(sessions, _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawn = new AgentSpawnTool(registry, router, parent);

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
            new TestAgentSessions(Router(provider), deliversCompletions: false), _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawned = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "worker",
            string.Empty);
        _ = await spawned.Send("initial", cancellationToken);
        var send = new AgentSendTool(registry, parent);

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
            new TestAgentSessions(Router(provider), deliversCompletions: false), _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "parent", cancellationToken);
        _ = await parent.Send("initial", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await parent.Wait(0, cancellationToken);
        var child = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "worker",
            string.Empty);
        var send = new AgentSendTool(registry, child);

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
            new TestAgentSessions(Router(provider), deliversCompletions: false), _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "parent-id", cancellationToken);
        _ = await parent.Send("initial", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await parent.Wait(0, cancellationToken);
        var child = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "worker",
            string.Empty);
        var send = new AgentSendTool(registry, child);

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
            new TestAgentSessions(Router(provider), deliversCompletions: false), _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawned = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "worker",
            string.Empty);
        _ = await spawned.Send("initial", cancellationToken);

        await provider.Arrived(cancellationToken);
        var sending = new AgentSendTool(registry, parent).Execute(
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
    public async Task Send_validates_arguments_size_and_rejects_unrelated_agents(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false), _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "parent", cancellationToken);
        var stranger = Session(provider, 0, "stranger", cancellationToken);
        var spawned = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "worker",
            string.Empty);
        _ = await spawned.Send("initial", cancellationToken);
        var send = new AgentSendTool(registry, parent);

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
        var invisible = (await new AgentSendTool(registry, stranger).Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"hello"}"""),
            Turn(stranger, Router(provider)),
            cancellationToken)).Text;
        var oversized = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"{{new string('x', (1024 * 1024) + 1)}}"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;

        _ = await Assert.That(malformed).StartsWith("error:");
        _ = await Assert.That(blank).IsEqualTo("error: no message given");
        _ = await Assert.That(missing).IsEqualTo("error: child agent not found: missing");
        _ = await Assert.That(invisible).IsEqualTo("error: only parent/child may be sent");
        _ = await Assert.That(invisible).DoesNotContain(spawned.SessionId);
        _ = await Assert.That(oversized).IsEqualTo("error: agent message exceeds 1048576 bytes");
        provider.Release();
    }

    [Test]
    public async Task Spawn_rejects_a_foreground_profile(CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        await using var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false), _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "parent", cancellationToken);
        var spawn = new AgentSpawnTool(registry, Router(provider), parent);

        var result = (await spawn.Execute(new ToolInvocation("test-call", """{"prompt":"work","agent":"build"}"""), Turn(parent, Router(provider)), cancellationToken)).Text;

        _ = await Assert.That(result).IsEqualTo("error: agent profile build cannot be spawned");
    }

    [Test]
    public async Task Spawn_inherits_runtime_capabilities_and_existing_descendants_follow_parent_security(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider();
        var sessions = new TestAgentSessions(Router(provider), deliversCompletions: true);
        await using var registry = TestModels.Registry(
            sessions, _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var capability = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var denied = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        var parent = Session(provider, 0, "parent", cancellationToken);
        parent.UpdateSelection(
            parent.Selection().RequestedModel,
            Profile(
                ModeRegistry.Build,
                readOnly: false,
                [new SandboxRule(capability, SandboxRuleAction.AllowWrite)]));

        var child = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "worker",
            string.Empty);
        var grandchild = registry.Spawn(
            child,
            Turn(child, Router(provider)),
            "worker",
            child.Selection().RequestedModel,
            "grandchild",
            string.Empty);

        _ = await Assert.That(child.ResolveSelection().SecurityProfile.AllowsWrite(capability)).IsTrue();
        _ = await Assert.That(grandchild.ResolveSelection().SecurityProfile.AllowsWrite(capability)).IsTrue();
        _ = await Assert.That(child.ResolveSelection().SecurityProfile.RuntimeCapabilities).IsNotEmpty();
        _ = await Assert.That(child.WriteGrants).IsNotSameReferenceAs(parent.WriteGrants);

        parent.UpdateSelection(
            parent.Selection().RequestedModel,
            Profile(ModeRegistry.Plan, readOnly: true, []));

        _ = await Assert.That(child.ResolveSelection().SecurityProfile.ReadOnly).IsTrue();
        _ = await Assert.That(grandchild.ResolveSelection().SecurityProfile.ReadOnly).IsTrue();
        _ = await Assert.That(child.ResolveSelection().SecurityProfile.AllowsWrite(capability)).IsFalse();
        _ = await Assert.That(grandchild.ResolveSelection().SecurityProfile.AllowsWrite(denied)).IsFalse();
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
            new TestAgentSessions(Router(provider), deliversCompletions: false), _broker, _repository, TestModels.ProfileRegistry(), cancellationToken);
        var parent = Session(provider, 0, "parent", cancellationToken);
        var spawn = new AgentSpawnTool(registry, Router(provider), parent);
        var idle = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "idle",
            string.Empty);
        _ = await idle.Send("become idle", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await idle.Wait(0, cancellationToken);

        var deepParent = Session(provider, 4, "deep-parent", cancellationToken);
        var tooDeep = (await new AgentSpawnTool(registry, Router(provider), deepParent).Execute(
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
        var registry = TestModels.Registry(
            new TestAgentSessions(Router(provider), deliversCompletions: false),
            _broker,
            _repository,
            TestModels.ProfileRegistry(),
            cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawned = registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "worker",
            string.Empty);
        _ = await spawned.Send("first", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await spawned.Wait(0, cancellationToken);
        var send = new AgentSendTool(registry, parent);
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
        _ = await Assert.That(() => registry.Spawn(
            parent,
            Turn(parent, Router(provider)),
            "worker",
            parent.Selection().RequestedModel,
            "worker",
            string.Empty))
            .Throws<AgentRegistryException>();
        var rejected = (await send.Execute(
            new ToolInvocation(
                "test-call",
                $$"""{"session_id":"{{spawned.SessionId}}","message":"again"}"""),
            Turn(parent, Router(provider)),
            cancellationToken)).Text;
        _ = await Assert.That(rejected).IsEqualTo("error: the user session is shutting down");
    }

    private static TestAgentProfile Profile(
        string id,
        bool readOnly,
        IReadOnlyList<SandboxRule> runtimeCapabilities) =>
        new(
            new AgentProfile(
                id,
                new ProfileConfig("Test prompt", "Test profile.", null, 1, 3, readOnly, true, []),
                [],
                [],
                new HashSet<string>(StringComparer.Ordinal)),
            SecurityProfile.Compose(readOnly, [], [], runtimeCapabilities));

    private static AgentTurnSelection Turn(AgentSession session, ModelRouter router)
    {
        var selection = session.Selection();
        return new AgentTurnSelection(
            selection.RequestedModel,
            router.Resolve(selection.RequestedModel.Value),
            selection.Profile,
            selection.SecurityProfile);
    }

    private static ModelRouter Router(SteppedProvider provider) =>
        Router(provider, []);

    private static ModelRouter Router(
        SteppedProvider provider,
        IReadOnlyList<ModelAliasDefinition> aliases)
    {
        var models = new[] { new LLMModel("model", provider.Id), new LLMModel("replacement", provider.Id) };
        var registry = new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>> { [provider.Id] = models });
        return new ModelRouter(registry, new ModelAliasCatalog(registry, aliases), "stepped/model");
    }

    private AgentSession Session(
        SteppedProvider provider,
        int depth,
        string sessionId,
        CancellationToken cancellationToken) =>
        Session(provider, depth, sessionId, depth == 0 ? string.Empty : "parent", cancellationToken);

    private AgentSession Session(
        SteppedProvider provider,
        int depth,
        string sessionId,
        string name,
        CancellationToken cancellationToken)
    {
        var router = Router(provider);
        var identity = depth == 0
            ? AgentIdentity.Main(sessionId, name)
            : AgentIdentity.Child(sessionId, "ancestor", "ancestor-agent", name, depth, AgentScope.Empty);
        var dependencies = TestModels.Dependencies(identity, _broker, _repository, cancellationToken);
        return new AgentSession(
            identity,
            new ModelSelector("stepped/model"),
            router,
            _broker,
            _repository,
            [],
            TestModels.MaterializePrompt(identity, ".", "."),
            new TodoCollection(identity.SessionId, _repository, _broker),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new Compactor(120_000),
            dependencies.ActiveWorkReminder,
            dependencies.Profile,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            dependencies.Status,
            dependencies.Registry,
            dependencies.Queues,
            cancellationToken);
    }

    private sealed class TestAgentProfile(IAgentProfile profile, SecurityProfile securityProfile) : IAgentProfile
    {
        public string Id => profile.Id;

        public string Prompt => profile.Prompt;

        public IReadOnlyList<string>? AllowedTools => profile.AllowedTools;

        public IReadOnlyList<string> DisabledTools => profile.DisabledTools;

        public int MaxTurns => profile.MaxTurns;

        public bool EnforceActiveWorkCompletion => profile.EnforceActiveWorkCompletion;

        public SecurityProfile SecurityProfile => securityProfile;
    }

    private sealed class UnsupportedAgentSessionFactory : IAgentSessionFactory
    {
        public IAgentSessionLease Create(
            AgentIdentity identity,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IAgentProfile profile,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            AgentRegistry registry,
            CancellationToken lifetime) =>
            throw new NotSupportedException("This test session does not support spawning subagents.");
    }

    private sealed class TestAgentSessions(ModelRouter router, bool deliversCompletions) : IAgentSessionFactory
    {
        private static readonly ConcurrentBag<UserSessionResources> Resources = [];
        private static readonly ConcurrentBag<ShellProcessOwners> ProcessOwners = [];
        private static readonly ConcurrentBag<AgentQueueCatalog> QueueCatalogs = [];
        private readonly List<AgentIdentity> _identities = [];
        private readonly List<ModelSelector> _models = [];

        public IReadOnlyList<AgentIdentity> Identities => _identities;

        public IReadOnlyList<ModelSelector> Models => _models;

        public List<IAgentProfile> Profiles { get; } = [];

        public List<SecurityProfile> SecurityProfiles { get; } = [];

        public IAgentSessionLease Create(
            AgentIdentity identity,
            ModelSelector model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            IAgentProfile profile,
            SecurityProfile securityProfile,
            RuntimeStatus status,
            AgentRegistry registry,
            CancellationToken lifetime)
        {
            _identities.Add(identity);
            _models.Add(model);
            Profiles.Add(profile);
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
                _ = queueCatalog.Register(AgentIdentity.Main(identity.ParentSessionId, identity.ParentSessionName));
            }

            var queues = queueCatalog.Register(identity);
            var completionRegistry = deliversCompletions
                ? registry
                : TestModels.Registry(
                    new UnsupportedAgentSessionFactory(),
                    eventBroker,
                    eventRepository,
                    TestModels.ProfileRegistry(),
                    lifetime);
            Resources.Add(resources);
            ProcessOwners.Add(processes);
            QueueCatalogs.Add(queueCatalog);

            var session = new AgentSession(
                identity,
                model,
                router,
                eventBroker,
                eventRepository,
                [],
                TestModels.MaterializePrompt(identity, ".", "."),
                new TodoCollection(identity.SessionId, eventRepository, eventBroker),
                new ToolOutputBlobStore(Path.GetTempPath()),
                new Compactor(120_000),
                new ActiveWorkCompletionReminder(identity.SessionId, registry, processOwner),
                profile,
                securityProfile,
                new RuntimeStatus(queueCatalog, processes, registry),
                completionRegistry,
                queues,
                lifetime);
            queues.Attach(session);
            return new AgentSessionLease(session);
        }
    }
}

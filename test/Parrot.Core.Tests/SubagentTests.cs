using System.Text.Json;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
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
        var sessions = new TestAgentSessions();
        await using var registry = new AgentRegistry(
            sessions, _broker, _repository, cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawn = new AgentSpawnTool(registry, parent, parent.Selection());
        var wait = new WaitAgentTool(registry);

        var startedJson = await spawn.Execute(
            """{"prompt":"do the subtask","name":"  Child Helper!  "}""", cancellationToken);
        using var started = JsonDocument.Parse(startedJson);
        var sessionId = started.RootElement.GetProperty("session_id").GetString() ?? string.Empty;

        _ = await Assert.That(started.RootElement.GetProperty("name").GetString()).IsEqualTo("child-helper");
        _ = await Assert.That(started.RootElement.GetProperty("status").GetString()).IsEqualTo("running");
        _ = await Assert.That(started.RootElement.GetProperty("depth").GetInt32()).IsEqualTo(1);
        _ = await Assert.That(sessionId).StartsWith("agent-session-");
        await provider.Arrived(cancellationToken);

        var yieldedJson = await wait.Execute(
            $$"""{"session_id":"{{sessionId}}","yield_after_ms":1}""", cancellationToken);
        using var yielded = JsonDocument.Parse(yieldedJson);
        _ = await Assert.That(yielded.RootElement.GetProperty("yielded").GetBoolean()).IsTrue();
        _ = await Assert.That(yielded.RootElement.GetProperty("status").GetString()).IsEqualTo("running");

        provider.Release();
        var completedJson = await wait.Execute(
            """{"session_id":"child-helper"}""", cancellationToken);
        using var completed = JsonDocument.Parse(completedJson);
        var retainedJson = await wait.Execute(
            $$"""{"session_id":"{{sessionId}}"}""", cancellationToken);
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

        var systemPrompt = provider.Requests.Single().Messages.Single(message => message.Role == LLMRole.System).Content;
        _ = await Assert.That(systemPrompt).Contains($"Child agent session: {sessionId}");
        _ = await Assert.That(systemPrompt).Contains("Parent agent session: agent");
        _ = await Assert.That(systemPrompt).Contains("Child agent name: child-helper");
    }

    [Test]
    public async Task Spawn_uses_the_selection_captured_before_parent_selection_changes(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        using var replacement = new SteppedProvider();
        var sessions = new TestAgentSessions();
        await using var registry = new AgentRegistry(
            sessions, _broker, _repository, cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        parent.UpdateSelection(parent.Selection().ResolvedModel, ModeProfile.Build(readOnly: false, [], []));
        var spawn = new AgentSpawnTool(registry, parent, parent.Selection());
        parent.UpdateSelection(
            new ProviderModel(replacement, new LLMModel("replacement", replacement.Id)),
            mode: null);

        _ = await spawn.Execute("""{"prompt":"do the subtask"}""", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();

        _ = await Assert.That(sessions.Models.Single().Model.Id).IsEqualTo("model");
        _ = await Assert.That(sessions.Modes.Single()).IsNull();
    }

    [Test]
    public async Task Send_steers_running_child_and_reuses_idle_session_for_follow_up(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "steered", []),
            LLMEvent.Completed("stop", 1, 0, 1, "followed up", []));
        await using var registry = new AgentRegistry(
            new TestAgentSessions(), _broker, _repository, cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawned = registry.Spawn(parent, parent.Selection(), "worker");
        _ = await spawned.Send("initial", cancellationToken);
        var send = new AgentSendTool(registry, parent, parent.Selection());

        await provider.Arrived(cancellationToken);
        var steeredJson = await send.Execute(
            """{"session_id":"worker","message":"steer now"}""", cancellationToken);
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

        var followedUpJson = await send.Execute(
            $$"""{"session_id":"{{spawned.SessionId}}","message":"follow up"}""", cancellationToken);
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
    public async Task Send_at_completion_boundary_is_delivered_once(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "second", []));
        await using var registry = new AgentRegistry(
            new TestAgentSessions(), _broker, _repository, cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawned = registry.Spawn(parent, parent.Selection(), "worker");
        _ = await spawned.Send("initial", cancellationToken);

        await provider.Arrived(cancellationToken);
        var sending = new AgentSendTool(registry, parent, parent.Selection()).Execute(
            $$"""{"session_id":"{{spawned.SessionId}}","message":"boundary"}""", cancellationToken);
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
    public async Task Send_validates_arguments_size_and_allows_any_agent(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        await using var registry = new AgentRegistry(
            new TestAgentSessions(), _broker, _repository, cancellationToken);
        var parent = Session(provider, 0, "parent", cancellationToken);
        var stranger = Session(provider, 0, "stranger", cancellationToken);
        var spawned = registry.Spawn(parent, parent.Selection(), "worker");
        _ = await spawned.Send("initial", cancellationToken);
        var send = new AgentSendTool(registry, parent, parent.Selection());

        var malformed = await send.Execute("{}", cancellationToken);
        var blank = await send.Execute(
            $$"""{"session_id":"{{spawned.SessionId}}","message":" "}""", cancellationToken);
        var missing = await send.Execute(
            """{"session_id":"missing","message":"hello"}""", cancellationToken);
        var invisible = await new AgentSendTool(registry, stranger, stranger.Selection()).Execute(
            $$"""{"session_id":"{{spawned.SessionId}}","message":"hello"}""", cancellationToken);
        var oversized = await send.Execute(
            $$"""{"session_id":"{{spawned.SessionId}}","message":"{{new string('x', (1024 * 1024) + 1)}}"}""",
            cancellationToken);

        _ = await Assert.That(malformed).StartsWith("error:");
        _ = await Assert.That(blank).IsEqualTo("error: no message given");
        _ = await Assert.That(missing).IsEqualTo("error: child agent not found: missing");
        _ = await Assert.That(invisible).Contains($"\"session_id\":\"{spawned.SessionId}\"");
        _ = await Assert.That(oversized).IsEqualTo("error: agent message exceeds 1048576 bytes");
        provider.Release();
    }

    [Test]
    public async Task Spawn_drops_runtime_capabilities_and_send_rejects_a_more_permissive_target(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 0, 1, "done", []));
        var sessions = new TestAgentSessions();
        await using var registry = new AgentRegistry(sessions, _broker, _repository, cancellationToken);
        var parent = Session(provider, 0, "parent", cancellationToken);
        var planArtifact = Path.Combine(Path.GetTempPath(), "plan.md");
        parent.UpdateSelection(
            parent.Selection().ResolvedModel,
            ModeProfile.Plan(Path.GetTempPath(), planArtifact, true, [], [], static () => { }));
        var spawned = registry.Spawn(parent, parent.Selection(), "worker");
        parent.UpdateSelection(
            parent.Selection().ResolvedModel,
            ModeProfile.Build(readOnly: false, [], []));
        var permissive = registry.Spawn(parent, parent.Selection(), "permissive");

        var rejected = await new AgentSendTool(registry, spawned, spawned.Selection()).Execute(
            $$"""{"session_id":"{{permissive.SessionId}}","message":"hello"}""",
            cancellationToken);

        _ = await Assert.That(sessions.SecurityProfiles[0].Rules).IsEmpty();
        _ = await Assert.That(rejected).IsEqualTo("error: cannot delegate to a more permissive agent");
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
        await using var registry = new AgentRegistry(
            new TestAgentSessions(), _broker, _repository, cancellationToken);
        var parent = Session(provider, 0, "parent", cancellationToken);
        var spawn = new AgentSpawnTool(registry, parent, parent.Selection());
        var idle = registry.Spawn(parent, parent.Selection(), "idle");
        _ = await idle.Send("become idle", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await idle.Wait(0, cancellationToken);

        var deepParent = Session(provider, 4, "deep-parent", cancellationToken);
        var tooDeep = await new AgentSpawnTool(registry, deepParent, deepParent.Selection()).Execute(
            """{"prompt":"too deep"}""", cancellationToken);

        _ = await Assert.That(tooDeep).IsEqualTo("error: subagent depth limit reached");
    }

    [Test]
    public async Task Disposing_the_registry_cancels_and_joins_an_active_follow_up(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 0, 1, "first", []),
            LLMEvent.Completed("stop", 1, 0, 1, "unreachable", []));
        var registry = new AgentRegistry(
            new TestAgentSessions(),
            _broker,
            _repository,
            cancellationToken);
        var parent = Session(provider, 0, "agent", cancellationToken);
        var spawned = registry.Spawn(parent, parent.Selection(), "worker");
        _ = await spawned.Send("first", cancellationToken);
        await provider.Arrived(cancellationToken);
        provider.Release();
        _ = await spawned.Wait(0, cancellationToken);
        var send = new AgentSendTool(registry, parent, parent.Selection());
        _ = await send.Execute(
            $$"""{"session_id":"{{spawned.SessionId}}","message":"wait forever"}""", cancellationToken);
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
        _ = await Assert.That(() => registry.Spawn(parent, parent.Selection(), "worker"))
            .Throws<AgentRegistryException>();
        var rejected = await send.Execute(
            $$"""{"session_id":"{{spawned.SessionId}}","message":"again"}""", cancellationToken);
        _ = await Assert.That(rejected).IsEqualTo("error: the user session is shutting down");
    }

    private AgentSession Session(
        SteppedProvider provider,
        int depth,
        string sessionId,
        CancellationToken cancellationToken) =>
        new(
            depth == 0
                ? AgentIdentity.Main(sessionId, string.Empty)
                : AgentIdentity.Child(sessionId, "ancestor", "parent", depth),
            new ProviderModel(provider, new LLMModel("model", provider.Id)),
            _broker,
            _repository,
            [],
            new SystemContextBuilder(".", "2026-07-24", string.Empty),
            new Compactor(120_000),
            mode: null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            status: null,
            cancellationToken);

    private sealed class TestAgentSessions : IAgentSessionFactory
    {
        private readonly List<AgentIdentity> _identities = [];
        private readonly List<ProviderModel> _models = [];
        private readonly List<ModeProfile?> _modes = [];

        public IReadOnlyList<AgentIdentity> Identities => _identities;

        public IReadOnlyList<ProviderModel> Models => _models;

        public IReadOnlyList<ModeProfile?> Modes => _modes;

        public List<SecurityProfile> SecurityProfiles { get; } = [];

        public AgentSession Create(
            AgentIdentity identity,
            ProviderModel model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            ModeProfile? mode,
            SecurityProfile securityProfile,
            RuntimeStatus? status,
            CancellationToken lifetime)
        {
            _identities.Add(identity);
            _models.Add(model);
            _modes.Add(mode);
            SecurityProfiles.Add(securityProfile);

            return new AgentSession(
                identity,
                model,
                eventBroker,
                eventRepository,
                [],
                new SystemContextBuilder(".", "2026-07-24", identity.Context),
                new Compactor(120_000),
                mode,
                securityProfile,
                status,
                lifetime);
        }
    }
}

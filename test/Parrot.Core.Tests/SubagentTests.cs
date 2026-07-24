using System.Text.Json;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
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
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 1, "child says hi", []));
        var sessions = new TestAgentSessions();
        await using var registry = new AgentRegistry(
            sessions, _broker, _repository, cancellationToken);
        var parent = Session(provider, depth: 0, cancellationToken);
        var spawn = new AgentSpawnTool(registry, parent);
        var wait = new WaitAgentTool(registry, parent);

        var startedJson = await spawn.Execute(
            """{"prompt":"do the subtask","name":"  Child Helper!  "}""", cancellationToken);
        using var started = JsonDocument.Parse(startedJson);
        var sessionId = started.RootElement.GetProperty("session_id").GetString() ?? string.Empty;

        _ = await Assert.That(started.RootElement.GetProperty("name").GetString()).IsEqualTo("child-helper");
        _ = await Assert.That(started.RootElement.GetProperty("status").GetString()).IsEqualTo("running");
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
    public async Task Registry_enforces_depth_and_per_parent_concurrency_limits(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 1, "one", []),
            LLMEvent.Completed("stop", 1, 1, "two", []),
            LLMEvent.Completed("stop", 1, 1, "three", []),
            LLMEvent.Completed("stop", 1, 1, "four", []));
        await using var registry = new AgentRegistry(
            new TestAgentSessions(), _broker, _repository, cancellationToken);
        var parent = Session(provider, depth: 0, cancellationToken, "parent");
        var spawn = new AgentSpawnTool(registry, parent);

        for (var index = 0; index < 4; index++)
        {
            var result = await spawn.Execute($$"""{"prompt":"child {{index}}"}""", cancellationToken);
            _ = await Assert.That(result).Contains("\"status\":\"running\"");
            await provider.Arrived(cancellationToken);
        }

        var tooManyForParent = await spawn.Execute("""{"prompt":"fifth"}""", cancellationToken);
        var tooDeep = await new AgentSpawnTool(
            registry, Session(provider, depth: 4, cancellationToken, "deep-parent")).Execute(
            """{"prompt":"too deep"}""", cancellationToken);

        _ = await Assert.That(tooManyForParent)
            .IsEqualTo("error: subagent concurrency limit reached for this parent");
        _ = await Assert.That(tooDeep).IsEqualTo("error: subagent depth limit reached");

        for (var index = 0; index < 4; index++)
        {
            provider.Release();
        }
    }

    [Test]
    public async Task Disposing_the_registry_cancels_and_joins_running_children(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(LLMEvent.Completed("stop", 1, 1, "unreachable", []));
        var registry = new AgentRegistry(
            new TestAgentSessions(),
            _broker,
            _repository,
            cancellationToken);
        var parent = Session(provider, depth: 0, cancellationToken);
        var spawned = registry.Spawn(parent, "wait forever", "worker");
        await provider.Arrived(cancellationToken);

        await registry.DisposeAsync();
        var terminal = await registry.Wait(parent, spawned.SessionId, 0, cancellationToken);

        _ = await Assert.That(terminal.Status).IsEqualTo(AgentTaskStatus.Canceled);
        _ = await Assert.That(terminal.Error).IsEqualTo("interrupted");
        var failed = _repository.Replay().Single(
            published => published.PayloadCase == Event.PayloadOneofCase.AgentFailed);
        _ = await Assert.That(failed.AgentSessionId).IsEqualTo(spawned.SessionId);
        _ = await Assert.That(failed.AgentFailed.ParentAgentSessionId).IsEqualTo("agent");
        _ = await Assert.That(failed.AgentFailed.Name).IsEqualTo("worker");
        _ = await Assert.That(failed.AgentFailed.Message).IsEqualTo("interrupted");
        _ = await Assert.That(() => registry.Spawn(parent, "again", "worker"))
            .Throws<AgentRegistryException>();
    }

    private AgentSession Session(
        ILLMProvider provider,
        int depth,
        CancellationToken cancellationToken,
        string sessionId = "agent") =>
        new(
            depth == 0
                ? AgentIdentity.Main(sessionId)
                : AgentIdentity.Child(sessionId, "ancestor", "parent", depth),
            provider,
            _broker,
            _repository,
            [],
            new SystemContextBuilder(".", "2026-07-24", string.Empty),
            new Compactor(120_000),
            cancellationToken)
        {
            Model = "model",
        };

    private sealed class TestAgentSessions : IAgentSessionFactory
    {
        private readonly List<AgentIdentity> _identities = [];

        public IReadOnlyList<AgentIdentity> Identities => _identities;

        public AgentSession Create(
            AgentIdentity identity,
            ILLMProvider provider,
            string model,
            EventBroker eventBroker,
            EventRepository eventRepository,
            CancellationToken lifetime)
        {
            _identities.Add(identity);

            return new AgentSession(
                identity,
                provider,
                eventBroker,
                eventRepository,
                [],
                new SystemContextBuilder(".", "2026-07-24", identity.Context),
                new Compactor(120_000),
                lifetime)
            {
                Model = model,
            };
        }
    }
}

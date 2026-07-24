using System.Text.Json;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
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
            sessions, new ProcessAgentConcurrency(), _broker, _repository, cancellationToken);
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
    }

    [Test]
    public async Task Registry_enforces_depth_per_parent_and_process_concurrency_limits(
        CancellationToken cancellationToken)
    {
        using var provider = new SteppedProvider(
            LLMEvent.Completed("stop", 1, 1, "one", []),
            LLMEvent.Completed("stop", 1, 1, "two", []),
            LLMEvent.Completed("stop", 1, 1, "three", []),
            LLMEvent.Completed("stop", 1, 1, "four", []),
            LLMEvent.Completed("stop", 1, 1, "five", []),
            LLMEvent.Completed("stop", 1, 1, "six", []),
            LLMEvent.Completed("stop", 1, 1, "seven", []),
            LLMEvent.Completed("stop", 1, 1, "eight", []));
        var concurrency = new ProcessAgentConcurrency();
        await using var firstRegistry = new AgentRegistry(
            new TestAgentSessions(), concurrency, _broker, _repository, cancellationToken);
        await using var secondRegistry = new AgentRegistry(
            new TestAgentSessions(), concurrency, _broker, _repository, cancellationToken);
        var firstParent = Session(provider, depth: 0, cancellationToken, "first-parent");
        var secondParent = Session(provider, depth: 0, cancellationToken, "second-parent");
        var firstSpawn = new AgentSpawnTool(firstRegistry, firstParent);
        var secondSpawn = new AgentSpawnTool(secondRegistry, secondParent);

        for (var index = 0; index < 4; index++)
        {
            var result = await firstSpawn.Execute($$"""{"prompt":"first {{index}}"}""", cancellationToken);
            _ = await Assert.That(result).Contains("\"status\":\"running\"");
            await provider.Arrived(cancellationToken);
        }

        var tooManyForParent = await firstSpawn.Execute("""{"prompt":"fifth"}""", cancellationToken);
        var tooDeep = await new AgentSpawnTool(
            firstRegistry, Session(provider, depth: 4, cancellationToken, "deep-parent")).Execute(
            """{"prompt":"too deep"}""", cancellationToken);

        for (var index = 0; index < 4; index++)
        {
            var result = await secondSpawn.Execute($$"""{"prompt":"second {{index}}"}""", cancellationToken);
            _ = await Assert.That(result).Contains("\"status\":\"running\"");
            await provider.Arrived(cancellationToken);
        }

        var processLimit = await new AgentSpawnTool(
            secondRegistry, Session(provider, depth: 0, cancellationToken, "third-parent")).Execute(
            """{"prompt":"ninth"}""", cancellationToken);

        _ = await Assert.That(tooManyForParent)
            .IsEqualTo("error: subagent concurrency limit reached for this parent");
        _ = await Assert.That(tooDeep).IsEqualTo("error: subagent depth limit reached");
        _ = await Assert.That(processLimit).IsEqualTo("error: subagent concurrency limit reached");

        for (var index = 0; index < 8; index++)
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
            new ProcessAgentConcurrency(),
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
            new SystemContextBuilder(".", "2026-07-24"),
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
                new SystemContextBuilder(".", "2026-07-24"),
                new Compactor(120_000),
                lifetime)
            {
                Model = model,
            };
        }
    }
}

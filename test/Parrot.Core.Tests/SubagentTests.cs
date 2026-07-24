using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class SubagentTests : IDisposable
{
    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventBroker _broker = new();

    public void Dispose()
    {
        _broker.Dispose();
        _database.Dispose();
    }

    // Spawning runs through the tool, because the tool is what holds both the
    // spawning session and the user session the child joins.
    [Test]
    [Arguments(0, "child says hi")]
    [Arguments(99, "error: subagent depth limit reached")]
    public async Task A_spawned_child_runs_to_completion_and_a_deep_spawn_refuses(
        int depth, string expected, CancellationToken cancellationToken)
    {
        var provider = new ScriptedProvider("child says hi");
        using var owner = new UserSession(
            "user", "model", new EventRepository(_database), new UnusedAgentSessions());
        var spawn = new AgentSpawnTool(owner, Session(provider, depth));

        var result = await spawn.Execute("""{"prompt":"do the subtask"}""", cancellationToken);

        _ = await Assert.That(result).IsEqualTo(expected);
    }

    private AgentSession Session(Parrot.Llm.ILLMProvider provider, int depth) =>
        new(
            "agent",
            provider,
            _broker,
            new EventRepository(_database),
            [],
            new SystemContextBuilder(".", "2026-07-24"),
            new Compactor(provider, 120_000),
            depth)
        {
            Model = "model",
        };
}

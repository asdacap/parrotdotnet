using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Process;
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

    [Test]
    public async Task A_spawned_child_runs_and_returns_its_final_text(CancellationToken cancellationToken)
    {
        var session = Session(new ScriptedProvider("child says hi"));

        var result = await session.Spawn("do the subtask", childDepth: 1, cancellationToken);

        _ = await Assert.That(result).IsEqualTo("child says hi");
    }

    [Test]
    public async Task Recursion_is_bounded_so_a_deep_spawn_refuses(CancellationToken cancellationToken)
    {
        var session = Session(new ScriptedProvider("should not run"));

        var result = await session.Spawn("too deep", childDepth: 99, cancellationToken);

        _ = await Assert.That(result).Contains("depth limit");
    }

    private AgentSession Session(Parrot.Llm.ILLMProvider provider) =>
        new(
            "agent",
            provider,
            _broker,
            new EventRepository(_database),
            new ToolRegistry([]),
            ".",
            new ProcessRunner(string.Empty),
            new SystemContextBuilder(".", "2026-07-24"),
            new Compactor(provider, 120_000),
            depth: 0)
        {
            Model = "model",
        };
}

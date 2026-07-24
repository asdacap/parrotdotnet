using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class CompactorAndContextTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "parrot-context-tests", Guid.NewGuid().ToString("n"));

    public CompactorAndContextTests() => Directory.CreateDirectory(_workspace);

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
        {
            Directory.Delete(_workspace, recursive: true);
        }
    }

    [Test]
    public async Task System_context_includes_platform_cwd_and_an_agents_file()
    {
        await File.WriteAllTextAsync(Path.Combine(_workspace, "AGENTS.md"), "PROJECT RULE: be terse.");

        var built = new SystemContextBuilder(_workspace, "2026-07-24", string.Empty).Build();

        _ = await Assert.That(built).Contains("2026-07-24");
        _ = await Assert.That(built).Contains(_workspace);
        _ = await Assert.That(built).Contains("PROJECT RULE: be terse.");
    }

    [Test]
    public async Task Agent_session_compaction_preserves_the_current_history(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var broker = new EventBroker();
        var provider = new ScriptedProvider("reply");
        var session = new AgentSession(
            AgentIdentity.Main("agent"),
            provider,
            broker,
            new EventRepository(database),
            [],
            new SystemContextBuilder(_workspace, "2026-07-24", string.Empty),
            new Compactor(tokenBudget: 0),
            mode: null,
            status: null,
            cancellationToken)
        {
            Model = "model",
        };

        _ = await session.Send(
            "keep this prompt", Identifier.MessageId(), Delivery.Steer, cancellationToken);
        _ = await session.ResultSettled();

        var inferenceRequest = provider.Requests.Single();
        _ = await Assert.That(inferenceRequest.Messages)
            .Contains(message => message.Role == LLMRole.User && message.Content == "keep this prompt");
    }

    [Test]
    public async Task Compaction_shrinks_history_and_keeps_the_recent_tail(CancellationToken cancellationToken)
    {
        var provider = new ScriptedProvider("SUMMARY OF EARLIER");

        // A budget of zero forces compaction; the four newest messages survive.
        var compactor = new Compactor(tokenBudget: 0);

        var history = new List<LLMMessage>();

        for (var index = 0; index < 20; index++)
        {
            history.Add(LLMMessage.User($"message {index}"));
        }

        _ = await Assert.That(compactor.ShouldCompact(history)).IsTrue();

        var compacted = await Compactor.Compact(provider, "model", history, cancellationToken);

        _ = await Assert.That(compacted.Count).IsLessThan(history.Count);
        _ = await Assert.That(compacted[0].Role).IsEqualTo(LLMRole.System);
        _ = await Assert.That(compacted[0].Content).Contains("SUMMARY OF EARLIER");

        // The tail is kept verbatim so the thread is not lost.
        _ = await Assert.That(compacted[^1].Content).IsEqualTo("message 19");
    }
}

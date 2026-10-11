using Parrot.Agent;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Queues;

namespace Parrot.Core.Tests;

internal sealed class AgentSessionServiceTests
{
    [Test]
    public async Task Encrypted_reasoning_metadata_flushes_text_and_is_not_coalesced()
    {
        var buffer = new StreamedFragmentBuffer();
        _ = buffer.Add(new Event { AgentSessionId = "agent", TextChunk = new TextChunk { Fragment = "first" } });
        _ = buffer.Add(new Event { AgentSessionId = "agent", TextChunk = new TextChunk { Fragment = "held" } });
        var received = new Event
        {
            AgentSessionId = "agent",
            EncryptedReasoningReceived = new EncryptedReasoningReceivedEvent(),
        };
        var ready = buffer.Add(received);
        _ = await Assert.That(ready.Count).IsEqualTo(2);
        _ = await Assert.That(ready[0].TextChunk.Fragment).IsEqualTo("held");
        _ = await Assert.That(ready[1]).IsSameReferenceAs(received);
        _ = await Assert.That(buffer.Add(received).Single()).IsSameReferenceAs(received);
        _ = await Assert.That(buffer.IsHolding).IsFalse();
    }

    [Test]
    public async Task Production_scope_keeps_the_registered_queue_identity_through_shutdown()
    {
        var directory = Path.Combine(Path.GetTempPath(), "parrot-session-services", Guid.NewGuid().ToString("n"));
        _ = Directory.CreateDirectory(directory);
        try
        {
            var provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            using var fixture = new ProductionSessionStoreFixture(directory, model, passThroughSandbox: false);
            await using var session = await fixture.Store.Open(fixture.Router.Resolve(model.Selector));
            var scope = session.Registry.SnapshotScopes().Single();
            var queues = scope.GetService<IAgentQueues>();

            _ = await Assert.That(scope.GetService<IAgentQueues>()).IsSameReferenceAs(queues);
            _ = await Assert.That(() => scope.GetService<IAgentSession>()).Throws<InvalidOperationException>();
            _ = await Assert.That(() => scope.GetService<AgentQueues>()).Throws<InvalidOperationException>();

            var disposal = scope.DisposeAsync();
            _ = await Assert.That(scope.GetService<IAgentQueues>()).IsSameReferenceAs(queues);
            await disposal;
            _ = await Assert.That(scope.GetService<IAgentQueues>()).IsSameReferenceAs(queues);
            _ = await Assert.That(queues.IsDisposed).IsTrue();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

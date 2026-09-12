using Parrot.Agent;
using Parrot.Queues;

namespace Parrot.Core.Tests;

internal sealed class AgentQueueTestFixture : IAsyncDisposable
{
    private readonly IChildRegistry _children;

    public AgentQueueTestFixture(AgentIdentity identity)
    {
        _children = new ChildRegistry(identity);
        Queues = new AgentQueues(identity, null, TestModels.Resources(), _children, static queueIdentity => new QueueInventory(queueIdentity), TestDiagnosticLog.Instance);
        Queues.Initialize();
    }

    public IAgentQueues Queues { get; }

    public async ValueTask DisposeAsync()
    {
        Queues.Dispose();
        await _children.DisposeChildren().ConfigureAwait(false);
    }
}

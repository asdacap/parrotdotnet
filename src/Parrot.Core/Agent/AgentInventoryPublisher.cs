using Parrot.Events;
using Parrot.Process;
using Parrot.Queues;

namespace Parrot.Agent;

internal sealed class AgentInventoryPublisher(
    ShellProcessOwner processes,
    AgentQueues queues,
    EventBroker events,
    string rootSessionId)
{
    public Task Run() => Task.WhenAll(PublishQueues(), PublishProcesses());

    private async Task PublishQueues()
    {
        using var subscription = queues.SubscribeInventory();
        await foreach (var snapshot in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            events.PublishInventory([.. QueueInventoryProtocol.Convert(snapshot, rootSessionId)]);
        }
    }

    private async Task PublishProcesses()
    {
        using var subscription = processes.SubscribeInventory();
        await foreach (var snapshot in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            events.PublishInventory([.. ShellProcessInventoryProtocol.Convert(snapshot)]);
        }
    }
}

using Parrot.Events;
using Parrot.Protocol;

namespace Parrot.Process;

internal sealed class ProcessSnapshotPublisher(IProcessOwner processes, IEventBroker events)
{
    public IReadOnlyList<Event> CaptureSnapshotEvents() =>
        [.. ShellProcessInventoryProtocol.Convert(processes.CaptureInventory())];

    public async Task Run()
    {
        using var subscription = processes.SubscribeInventory();
        await foreach (var snapshot in subscription.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            events.PublishProcessSnapshot([.. ShellProcessInventoryProtocol.Convert(snapshot)]);
        }
    }
}

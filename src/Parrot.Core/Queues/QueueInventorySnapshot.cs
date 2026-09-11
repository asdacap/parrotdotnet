namespace Parrot.Queues;

internal sealed record QueueInventorySnapshot(
    string OwnerAgentSessionId,
    string InventoryInstanceId,
    ulong Revision,
    bool Removed,
    IReadOnlyList<QueueState> Queues);

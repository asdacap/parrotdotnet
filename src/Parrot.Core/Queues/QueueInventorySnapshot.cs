namespace Parrot.Queues;

internal sealed record QueueInventorySnapshot(ulong Revision, IReadOnlyList<QueueState> Queues);

namespace Parrot.Queues;

internal readonly record struct QueueTakeResult(IReadOnlyList<string> Items, QueueInfo Info);

namespace Parrot.Queues;

internal readonly record struct QueueTryTakeResult(
    bool Acquired,
    IReadOnlyList<string> Items,
    QueueInfo? Info);

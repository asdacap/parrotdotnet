namespace Parrot.Queues;

internal sealed record QueueInfo(string Path, string Name, string Description, int Size, bool Monitored);

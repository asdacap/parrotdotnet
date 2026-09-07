namespace Parrot.Queues;

internal sealed record QueueInfo(string Path, string Name, string Description, int Size, bool Monitored, bool Closed)
{
    public static QueueInfo FromMetadata(string path, QueueMetadata metadata, int size, bool monitored) =>
        new(path, metadata.Name, metadata.Description ?? string.Empty, size, monitored, metadata.Closed);
}

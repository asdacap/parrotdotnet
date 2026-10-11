namespace Parrot.Queues;

internal sealed record QueueInfo(string Path, string Name, string Description, int Size, bool Closed)
{
    public static QueueInfo FromMetadata(string path, QueueMetadata metadata, int size) =>
        new(path, metadata.Name, metadata.Description ?? string.Empty, size, metadata.Closed);
}

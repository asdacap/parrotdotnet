namespace Parrot.Queues;

public sealed class QueueEmptyException : QueueException
{
    public QueueEmptyException()
    {
    }

    public QueueEmptyException(string message)
        : base(message)
    {
    }

    public QueueEmptyException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    internal QueueEmptyException(QueueInfo info)
        : base($"queue: '{info.Name}' is empty") => Info = info;

    internal QueueInfo? Info { get; }
}

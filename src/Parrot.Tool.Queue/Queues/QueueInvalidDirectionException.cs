namespace Parrot.Queues;

public sealed class QueueInvalidDirectionException : QueueException
{
    public QueueInvalidDirectionException()
    {
    }

    public QueueInvalidDirectionException(string message)
        : base(message)
    {
    }

    public QueueInvalidDirectionException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

namespace Parrot.Queues;

public sealed class QueueClosedException : QueueException
{
    public QueueClosedException()
    {
    }

    public QueueClosedException(string message)
        : base(message)
    {
    }

    public QueueClosedException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

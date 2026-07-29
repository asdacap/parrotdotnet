namespace Parrot.Queues;

public sealed class QueueInvalidCountException : QueueException
{
    public QueueInvalidCountException()
    {
    }

    public QueueInvalidCountException(string message)
        : base(message)
    {
    }

    public QueueInvalidCountException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

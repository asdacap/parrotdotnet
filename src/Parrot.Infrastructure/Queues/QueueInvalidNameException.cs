namespace Parrot.Queues;

public sealed class QueueInvalidNameException : QueueException
{
    public QueueInvalidNameException()
    {
    }

    public QueueInvalidNameException(string message)
        : base(message)
    {
    }

    public QueueInvalidNameException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

namespace Parrot.Queues;

public sealed class QueueAlreadyExistsException : QueueException
{
    public QueueAlreadyExistsException()
    {
    }

    public QueueAlreadyExistsException(string message)
        : base(message)
    {
    }

    public QueueAlreadyExistsException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

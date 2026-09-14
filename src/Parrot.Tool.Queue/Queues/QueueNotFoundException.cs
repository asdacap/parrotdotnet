namespace Parrot.Queues;

public sealed class QueueNotFoundException : QueueException
{
    public QueueNotFoundException()
    {
    }

    public QueueNotFoundException(string message)
        : base(message)
    {
    }

    public QueueNotFoundException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

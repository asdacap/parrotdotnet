namespace Parrot.Cli;

internal sealed class TransportSocketActiveException : InvalidOperationException
{
    public TransportSocketActiveException()
    {
    }

    public TransportSocketActiveException(string message)
        : base(message)
    {
    }

    public TransportSocketActiveException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

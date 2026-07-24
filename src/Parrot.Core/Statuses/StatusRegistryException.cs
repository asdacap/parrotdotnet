namespace Parrot.Statuses;

public sealed class StatusRegistryException : Exception
{
    public StatusRegistryException()
    {
    }

    public StatusRegistryException(string message)
        : base(message)
    {
    }

    public StatusRegistryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

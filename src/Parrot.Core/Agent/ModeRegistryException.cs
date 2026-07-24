namespace Parrot.Agent;

public sealed class ModeRegistryException : Exception
{
    public ModeRegistryException()
    {
    }

    public ModeRegistryException(string message)
        : base(message)
    {
    }

    public ModeRegistryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

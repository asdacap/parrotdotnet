namespace Parrot.Agent;

public sealed class ChildNameConflictException : Exception
{
    public ChildNameConflictException()
    {
    }

    public ChildNameConflictException(string message)
        : base(message)
    {
    }

    public ChildNameConflictException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

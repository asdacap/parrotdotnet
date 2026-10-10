namespace Parrot.Llm.Wire;

public sealed class ProviderRequestTooLargeException : Exception
{
    public ProviderRequestTooLargeException()
    {
    }

    public ProviderRequestTooLargeException(string message)
        : base(message)
    {
    }

    public ProviderRequestTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

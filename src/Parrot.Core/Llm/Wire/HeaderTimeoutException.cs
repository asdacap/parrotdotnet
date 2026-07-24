namespace Parrot.Llm.Wire;

// A provider did not return response headers within the deadline. The response
// body is not covered by this timeout. Retryable: no client-visible output was
// produced, so a fresh attempt cannot duplicate any.
public sealed class HeaderTimeoutException : Exception
{
    public HeaderTimeoutException(TimeSpan timeout)
        : base($"provider: response headers timed out after {timeout}") =>
        Timeout = timeout;

    public HeaderTimeoutException(string message)
        : base(message)
    {
    }

    public HeaderTimeoutException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public HeaderTimeoutException()
    {
    }

    public TimeSpan Timeout { get; }
}

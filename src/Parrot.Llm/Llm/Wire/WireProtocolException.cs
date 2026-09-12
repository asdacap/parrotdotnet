namespace Parrot.Llm.Wire;

// A malformed or truncated provider stream: an SSE framing error or a wire
// adapter that reached the end without a terminal event.
public sealed class WireProtocolException : Exception
{
    public WireProtocolException(string message)
        : base(message)
    {
    }

    public WireProtocolException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public WireProtocolException()
    {
    }
}

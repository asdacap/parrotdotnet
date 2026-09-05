namespace Parrot.Llm.Wire;

public sealed class ResponsesWebSocketTransportException : IOException
{
    public ResponsesWebSocketTransportException()
    {
    }

    public ResponsesWebSocketTransportException(string message)
        : base(message)
    {
    }

    public ResponsesWebSocketTransportException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

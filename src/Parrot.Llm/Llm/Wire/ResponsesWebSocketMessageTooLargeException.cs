namespace Parrot.Llm.Wire;

public sealed class ResponsesWebSocketMessageTooLargeException : Exception
{
    public ResponsesWebSocketMessageTooLargeException()
    {
    }

    public ResponsesWebSocketMessageTooLargeException(string message)
        : base(message)
    {
    }

    public ResponsesWebSocketMessageTooLargeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

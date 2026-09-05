namespace Parrot.Llm.Wire;

public sealed class ResponsesWebSocketUpgradeException : Exception
{
    public ResponsesWebSocketUpgradeException()
    {
    }

    public ResponsesWebSocketUpgradeException(string message)
        : base(message)
    {
    }

    public ResponsesWebSocketUpgradeException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ResponsesWebSocketUpgradeException(
        int statusCode,
        string message,
        Exception innerException)
        : base(message, innerException) => StatusCode = statusCode;

    public int StatusCode { get; }
}

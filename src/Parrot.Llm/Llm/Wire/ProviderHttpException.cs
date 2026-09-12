namespace Parrot.Llm.Wire;

// A bounded, structured representation of a non-success HTTP response. The
// Type/Code fields are what the error classifier reads to decide whether a
// failure is a permanent usage limit or a transient overload.
public sealed class ProviderHttpException : Exception
{
    public ProviderHttpException(int statusCode, string errorType, string errorCode, string detail)
        : this(statusCode, errorType, errorCode, detail, string.Empty)
    {
    }

    public ProviderHttpException(
        int statusCode,
        string errorType,
        string errorCode,
        string detail,
        string responseBody)
        : base(Compose(statusCode, errorType, errorCode, detail))
    {
        StatusCode = statusCode;
        ErrorType = errorType;
        ErrorCode = errorCode;
        Detail = detail;
        ResponseBody = responseBody;
    }

    public ProviderHttpException(string message)
        : base(message)
    {
    }

    public ProviderHttpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProviderHttpException()
    {
    }

    public int StatusCode { get; }

    public string ErrorType { get; } = string.Empty;

    public string ErrorCode { get; } = string.Empty;

    public string Detail { get; } = string.Empty;

    public string ResponseBody { get; } = string.Empty;

    private static string Compose(int statusCode, string errorType, string errorCode, string detail)
    {
        var message = $"provider request failed with HTTP {statusCode}";

        if (errorType.Length > 0)
        {
            message += $" ({errorType})";
        }

        if (errorCode.Length > 0)
        {
            message += $" [{errorCode}]";
        }

        if (detail.Length > 0)
        {
            message += $": {detail}";
        }

        return message;
    }
}

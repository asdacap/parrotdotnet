namespace Parrot.Llm.Wire;

// A structured failure delivered inside an otherwise successful response stream
// (the wire adapter's ProviderError event, raised as an exception once the
// retry layer decides it is terminal).
public sealed class ProviderResponseException : Exception
{
    public ProviderResponseException(string errorType, string errorCode, string detail)
        : this(errorType, errorCode, detail, string.Empty)
    {
    }

    public ProviderResponseException(string errorType, string errorCode, string detail, string responseBody)
        : base(Compose(errorType, errorCode, detail))
    {
        ErrorType = errorType;
        ErrorCode = errorCode;
        Detail = detail;
        ResponseBody = responseBody;
    }

    public ProviderResponseException(string message)
        : base(message)
    {
    }

    public ProviderResponseException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public ProviderResponseException()
    {
    }

    public string ErrorType { get; } = string.Empty;

    public string ErrorCode { get; } = string.Empty;

    public string Detail { get; } = string.Empty;

    public string ResponseBody { get; } = string.Empty;

    private static string Compose(string errorType, string errorCode, string detail)
    {
        var message = detail.Length > 0 ? detail : "provider error";

        if (errorType.Length > 0)
        {
            message += $" ({errorType})";
        }

        if (errorCode.Length > 0)
        {
            message += $" [{errorCode}]";
        }

        return message;
    }
}

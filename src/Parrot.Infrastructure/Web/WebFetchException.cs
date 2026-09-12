namespace Parrot.Web;

public sealed class WebFetchException : Exception
{
    public WebFetchException()
    {
    }

    public WebFetchException(string message)
        : base(message)
    {
    }

    public WebFetchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

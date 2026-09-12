namespace Parrot.Llm;

internal sealed class ProviderHttpClient : IDisposable
{
    private readonly HttpClientHandler _handler;

    public ProviderHttpClient(HttpClientHandler handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        _handler = handler;
        Client = new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public HttpClient Client { get; }

    public void Dispose()
    {
        Client.Dispose();
        _handler.Dispose();
    }
}

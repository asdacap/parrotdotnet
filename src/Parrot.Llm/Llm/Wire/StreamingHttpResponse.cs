namespace Parrot.Llm.Wire;

internal sealed class StreamingHttpResponse(
    Stream content,
    TimeSpan idleTimeout,
    long limit,
    IDisposable owner,
    IReadOnlyDictionary<string, string> headers,
    IProviderAttemptDiagnostics? attempt) : IAsyncDisposable
{
    public Stream Content { get; } = new BoundedStream(new IdleTimeoutStream(content, idleTimeout, TimeProvider.System), limit, owner) { Attempt = attempt };

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

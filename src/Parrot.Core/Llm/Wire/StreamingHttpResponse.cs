namespace Parrot.Llm.Wire;

internal sealed class StreamingHttpResponse(
    Stream content,
    long limit,
    IDisposable owner,
    IReadOnlyDictionary<string, string> headers) : IAsyncDisposable
{
    public Stream Content { get; } = new BoundedStream(content, limit, owner);

    public IReadOnlyDictionary<string, string> Headers { get; } = headers;

    public ValueTask DisposeAsync() => Content.DisposeAsync();
}

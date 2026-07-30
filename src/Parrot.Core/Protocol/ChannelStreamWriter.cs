using System.Threading.Channels;
using Grpc.Core;

namespace Parrot.Protocol;

// Both halves of an in-process stream: the service writes, the client reads.
internal sealed class ChannelStreamWriter<T> : IServerStreamWriter<T>, IAsyncStreamReader<T>
    where T : class
{
    private readonly Channel<T> _messages = Channel.CreateUnbounded<T>();
    private T? _read;
    private Exception? _fault;

    public WriteOptions? WriteOptions { get; set; }

    public IAsyncStreamReader<T> Reader => this;

    // Explicit: the interface promises a non-null Current, but only once
    // MoveNext has returned true. Throwing states that, where "default!" would
    // have asserted it and PARROT0003 rightly refuses the assertion.
    T IAsyncStreamReader<T>.Current =>
        _read ?? throw new InvalidOperationException("MoveNext has not returned true");

    public Task WriteAsync(T message) => _messages.Writer.WriteAsync(message).AsTask();

    public bool TryWrite(T message) => _messages.Writer.TryWrite(message);

    // The cancellable overload is a default interface method that throws unless
    // the implementation provides it. A channel write takes a token natively.
    public Task WriteAsync(T message, CancellationToken cancellationToken) =>
        _messages.Writer.WriteAsync(message, cancellationToken).AsTask();

    public void Complete() => _messages.Writer.TryComplete();

    // A dispatch that throws must not look like a stream that ended. Without
    // this the client sees an empty, successful call.
    public void Fault(Exception failure)
    {
        _fault = failure;
        _ = _messages.Writer.TryComplete();
    }

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await _messages.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_messages.Reader.TryRead(out var message))
            {
                _read = message;
                return true;
            }
        }

        return _fault is null ? false : throw _fault;
    }
}

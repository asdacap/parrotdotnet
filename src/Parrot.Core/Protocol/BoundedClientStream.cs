using System.Threading.Channels;
using Grpc.Core;

namespace Parrot.Protocol;

internal sealed class BoundedClientStream<T> : IClientStreamWriter<T>, IAsyncStreamReader<T>
    where T : class
{
    private readonly Channel<T> _messages = Channel.CreateBounded<T>(new BoundedChannelOptions(4)
    {
        FullMode = BoundedChannelFullMode.Wait,
        SingleReader = true,
        SingleWriter = true,
    });

    private T? _current;

    public WriteOptions? WriteOptions { get; set; }

    T IAsyncStreamReader<T>.Current =>
        _current ?? throw new InvalidOperationException("MoveNext has not returned true");

    public Task WriteAsync(T message) => _messages.Writer.WriteAsync(message).AsTask();

    public Task WriteAsync(T message, CancellationToken cancellationToken) =>
        _messages.Writer.WriteAsync(message, cancellationToken).AsTask();

    public Task CompleteAsync()
    {
        Stop();
        return Task.CompletedTask;
    }

    public void Stop() => _ = _messages.Writer.TryComplete();

    public async Task<bool> MoveNext(CancellationToken cancellationToken)
    {
        while (await _messages.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (_messages.Reader.TryRead(out var message))
            {
                _current = message;
                return true;
            }
        }

        return false;
    }
}

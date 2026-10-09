using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedListenBinding : IAsyncDisposable
{
    private readonly PreparedSessionStream _stream;
    private bool _disposed;

    private EnhancedListenBinding(
        PreparedSessionStream stream,
        Func<AsyncServerStreamingCall<Event>, Func<bool>, CancellationToken, Task> render)
    {
        _stream = stream;
        Rendering = render(stream.Call, () => stream.IsReplaying, stream.Token);
    }

    public CancellationToken Token => _stream.Token;

    public Task Rendering { get; }

    public static EnhancedListenBinding Open(
        GeneratedParrot.ParrotClient client,
        string userSessionId,
        Func<AsyncServerStreamingCall<Event>, Func<bool>, CancellationToken, Task> render,
        CancellationToken cancellationToken) =>
        new(PreparedSessionStream.OpenLive(client, userSessionId, cancellationToken), render);

    public static EnhancedListenBinding StartPrepared(
        PreparedSessionStream stream,
        Func<AsyncServerStreamingCall<Event>, Func<bool>, CancellationToken, Task> render) => new(stream, render);

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _stream.Cancel().ConfigureAwait(false);
        try
        {
            await Rendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stream.Token.IsCancellationRequested)
        {
        }
        catch (RpcException failure) when (_stream.Token.IsCancellationRequested && failure.StatusCode == StatusCode.Cancelled)
        {
        }
        finally
        {
            await _stream.DisposeAsync().ConfigureAwait(false);
        }
    }
}

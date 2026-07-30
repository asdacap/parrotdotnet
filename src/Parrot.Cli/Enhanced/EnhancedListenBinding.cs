using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedListenBinding : IAsyncDisposable
{
    private readonly AsyncServerStreamingCall<Event> _call;
    private readonly CancellationTokenSource _cancellation;
    private bool _disposed;

    private EnhancedListenBinding(
        AsyncServerStreamingCall<Event> call,
        CancellationTokenSource cancellation,
        Func<AsyncServerStreamingCall<Event>, CancellationToken, Task> render)
    {
        _call = call;
        _cancellation = cancellation;
        Rendering = render(call, cancellation.Token);
    }

    public CancellationToken Token => _cancellation.Token;

    public Task Rendering { get; }

    public static EnhancedListenBinding Open(
        GeneratedParrot.ParrotClient client,
        string userSessionId,
        Func<AsyncServerStreamingCall<Event>, CancellationToken, Task> render,
        CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        try
        {
            var call = client.Listen(
                new ListenRequest { UserSessionId = userSessionId },
                cancellationToken: cancellation.Token);
            return new(call, cancellation, render);
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await _cancellation.CancelAsync().ConfigureAwait(false);
        await Rendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        _call.Dispose();
        _cancellation.Dispose();
    }
}

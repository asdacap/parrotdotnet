using Grpc.Core;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class PreparedSessionStream : IAsyncDisposable
{
    private readonly CancellationTokenSource _cancellation;
    private readonly SessionReplayContext _replay;
    private bool _disposed;

    private PreparedSessionStream(
        CancellationTokenSource cancellation, AsyncServerStreamingCall<Event> call, SessionReplayContext replay)
    {
        _cancellation = cancellation;
        Call = call;
        _replay = replay;
    }

    public AsyncServerStreamingCall<Event> Call { get; }

    public CancellationToken Token => _cancellation.Token;

    public bool IsReplaying => _replay.IsReplaying;

    public static PreparedSessionStream OpenLive(
        GeneratedParrot.ParrotClient client,
        string userSessionId,
        CancellationToken lifetimeToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        try
        {
            var call = client.Listen(new ListenRequest { UserSessionId = userSessionId }, cancellationToken: cancellation.Token);
            return new PreparedSessionStream(cancellation, call, new SessionReplayContext(false));
        }
        catch
        {
            cancellation.Dispose();
            throw;
        }
    }

    public static async Task<PreparedSessionStream> Open(
        GeneratedParrot.ParrotClient client,
        string userSessionId,
        CancellationToken lifetimeToken,
        CancellationToken cancellationToken)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        AsyncServerStreamingCall<Event>? call = null;
        try
        {
            call = client.Listen(
                new ListenRequest { UserSessionId = userSessionId, Replay = true },
                cancellationToken: cancellation.Token);
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token, cancellationToken);
            if (!await call.ResponseStream.MoveNext(preparation.Token).ConfigureAwait(false))
            {
                throw new RpcException(new Status(StatusCode.Unavailable, "the selected session stream ended before loading"));
            }

            var replay = new SessionReplayContext(true);
            var reader = new BufferedSessionReader(call.ResponseStream, call.ResponseStream.Current, replay);
            var buffered = new AsyncServerStreamingCall<Event>(
                reader, call.ResponseHeadersAsync, call.GetStatus, call.GetTrailers, call.Dispose);
            var result = new PreparedSessionStream(cancellation, buffered, replay);
            cancellation = null;
            call = null;
            return result;
        }
        catch
        {
            if (cancellation is not null)
            {
                await cancellation.CancelAsync().ConfigureAwait(false);
            }

            throw;
        }
        finally
        {
            call?.Dispose();
            cancellation?.Dispose();
        }
    }

    public void SetReplayCompletion(Action<bool> complete) => _replay.SetCompletion(complete);

    public Task Cancel() => _cancellation.CancelAsync();

    public async ValueTask DisposeAsync()
    {
        if (!_disposed)
        {
            _disposed = true;
            await _cancellation.CancelAsync().ConfigureAwait(false);
            Call.Dispose();
            _cancellation.Dispose();
        }
    }

    private sealed class BufferedSessionReader(IAsyncStreamReader<Event> reader, Event first, SessionReplayContext replay) : IAsyncStreamReader<Event>
    {
        private bool _buffered = true;

        public Event Current => _buffered ? first : reader.Current;

        public async Task<bool> MoveNext(CancellationToken cancellationToken)
        {
            if (_buffered)
            {
                _buffered = false;
                replay.Observe(first);
                return true;
            }

            var moved = await reader.MoveNext(cancellationToken).ConfigureAwait(false);
            if (moved)
            {
                replay.Observe(reader.Current);
            }

            return moved;
        }
    }
}

using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Parrot.Llm.Wire;

internal sealed class ResponsesWebSocket(
    WebSocket socket,
    IReadOnlyDictionary<string, string> responseHeaders,
    TimeSpan idleTimeout,
    int maximumRequestBytes,
    TimeProvider timeProvider) : IAsyncDisposable
{
    public const int FrameBytes = 1 << 20;

    // The ChatGPT endpoint closes larger messages with 1009 (Message Too Big).
    public const int MaximumMessageBytes = 16 << 20;

    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    // The ChatGPT endpoint closes sockets idle for about 70s with 1011, which is only seen on the next send.
    public static readonly TimeSpan ReuseLimit = TimeSpan.FromSeconds(60);

    private long _lastExchange = timeProvider.GetTimestamp();

    public IReadOnlyDictionary<string, string> ResponseHeaders { get; } = responseHeaders;

    public WebSocketState State => socket.State;

    public bool Stale => timeProvider.GetElapsedTime(_lastExchange) >= ReuseLimit;

    public async IAsyncEnumerable<LLMEvent> Send(
        byte[] request,
        ResponsesAdapter.ParseState response,
        IProviderAttemptDiagnostics? attempt,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (request.Length > maximumRequestBytes)
        {
            throw new ProviderHttpException($"provider: request exceeds {maximumRequestBytes} bytes");
        }

        attempt?.RecordRequestBytes(request.Length);
        if (request.Length > MaximumMessageBytes)
        {
            throw new ResponsesWebSocketMessageTooLargeException($"WebSocket request exceeds {MaximumMessageBytes} bytes.");
        }

        try
        {
            for (var offset = 0; offset < request.Length; offset += FrameBytes)
            {
                var frame = request.AsMemory(offset, Math.Min(FrameBytes, request.Length - offset));
                var flags = offset + frame.Length == request.Length ? WebSocketMessageFlags.EndOfMessage : WebSocketMessageFlags.None;
                await WithIdleTimeout(
                    token => socket.SendAsync(frame, WebSocketMessageType.Text, flags, token).AsTask(),
                    cancellationToken).ConfigureAwait(false);
            }

            var totalBytes = 0L;
            var message = new ArrayBufferWriter<byte>();
            while (!response.Done)
            {
                await ReceiveText(message, attempt, cancellationToken).ConfigureAwait(false);
                totalBytes += message.WrittenCount;
                if (totalBytes > HttpStreaming.MaxStreamBytes)
                {
                    throw new WireProtocolException($"responses: provider stream exceeds {HttpStreaming.MaxStreamBytes} bytes");
                }

                var data = Encoding.UTF8.GetString(message.WrittenSpan);
                foreach (var published in response.Consume(data))
                {
                    yield return published;
                }
            }

            _lastExchange = timeProvider.GetTimestamp();
            foreach (var published in response.CompleteEvents())
            {
                yield return published;
            }
        }
        finally
        {
            if (!response.Done)
            {
                socket.Abort();
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        socket.Abort();
        socket.Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task ReceiveText(
        ArrayBufferWriter<byte> writer,
        IProviderAttemptDiagnostics? attempt,
        CancellationToken cancellationToken)
    {
        writer.ResetWrittenCount();
        while (true)
        {
            var memory = writer.GetMemory(8192);
            var received = await WithIdleTimeout(
                token => socket.ReceiveAsync(memory, token).AsTask(), cancellationToken).ConfigureAwait(false);

            if (received.MessageType != WebSocketMessageType.Close)
            {
                attempt?.RecordResponseBytes(received.Count);
            }

            if (received.MessageType == WebSocketMessageType.Close)
            {
                if (socket.CloseStatus is { } status)
                {
                    attempt?.RecordCloseStatus((int)status);
                }

                throw new WireProtocolException(
                    $"responses: websocket closed before a terminal event (status {(int?)socket.CloseStatus}: {socket.CloseStatusDescription})");
            }

            if (received.MessageType != WebSocketMessageType.Text)
            {
                throw new WireProtocolException("responses: unexpected binary websocket event");
            }

            writer.Advance(received.Count);
            if (writer.WrittenCount > HttpStreaming.MaxEventBytes)
            {
                throw new WireProtocolException($"responses: websocket event exceeds {HttpStreaming.MaxEventBytes} bytes");
            }

            if (received.EndOfMessage)
            {
                return;
            }
        }
    }

    private async Task<T> WithIdleTimeout<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken)
    {
        using var idleSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleSource.CancelAfter(idleTimeout);
        try
        {
            return await operation(idleSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WireProtocolException("responses: idle timeout waiting for websocket");
        }
    }

    private async Task WithIdleTimeout(
        Func<CancellationToken, Task> operation,
        CancellationToken cancellationToken)
    {
        using var idleSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idleSource.CancelAfter(idleTimeout);
        try
        {
            await operation(idleSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new WireProtocolException("responses: idle timeout waiting for websocket");
        }
    }
}

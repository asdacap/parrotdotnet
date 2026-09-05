using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;

namespace Parrot.Llm.Wire;

internal sealed class ResponsesWebSocket(
    WebSocket socket,
    IReadOnlyDictionary<string, string> responseHeaders,
    TimeSpan idleTimeout) : IAsyncDisposable
{
    public static readonly TimeSpan DefaultIdleTimeout = TimeSpan.FromMinutes(5);

    public IReadOnlyDictionary<string, string> ResponseHeaders { get; } = responseHeaders;

    public WebSocketState State => socket.State;

    public async IAsyncEnumerable<LLMEvent> Send(
        byte[] request,
        ResponsesAdapter.ParseState response,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        if (request.Length > HttpStreaming.MaxRequestBytes)
        {
            throw new ProviderHttpException($"provider: request exceeds {HttpStreaming.MaxRequestBytes} bytes");
        }

        try
        {
            await WithIdleTimeout(
                token => socket.SendAsync(request, WebSocketMessageType.Text, WebSocketMessageFlags.EndOfMessage, token).AsTask(),
                cancellationToken).ConfigureAwait(false);

            var totalBytes = 0L;
            while (!response.Done)
            {
                var message = await ReceiveText(cancellationToken).ConfigureAwait(false);
                totalBytes += message.Length;
                if (totalBytes > HttpStreaming.MaxStreamBytes)
                {
                    throw new WireProtocolException($"responses: provider stream exceeds {HttpStreaming.MaxStreamBytes} bytes");
                }

                var data = Encoding.UTF8.GetString(message);
                foreach (var published in response.Consume(data))
                {
                    yield return published;
                }
            }

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

    private async Task<byte[]> ReceiveText(CancellationToken cancellationToken)
    {
        var writer = new ArrayBufferWriter<byte>();
        while (true)
        {
            var memory = writer.GetMemory(8192);
            var received = await WithIdleTimeout(
                token => socket.ReceiveAsync(memory, token).AsTask(), cancellationToken).ConfigureAwait(false);

            if (received.MessageType == WebSocketMessageType.Close)
            {
                throw new WireProtocolException("responses: websocket closed before a terminal event");
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
                return writer.WrittenSpan.ToArray();
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

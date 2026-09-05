using System.Net.WebSockets;

namespace Parrot.Llm.Wire;

internal sealed class ResponsesWebSocketConnector : IResponsesWebSocketConnector
{
    public async Task<WebSocket> Connect(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        foreach (var (name, value) in headers)
        {
            socket.Options.SetRequestHeader(name, value);
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (timeout > TimeSpan.Zero)
        {
            timeoutSource.CancelAfter(timeout);
        }

        try
        {
            await socket.ConnectAsync(endpoint, timeoutSource.Token).ConfigureAwait(false);
            return socket;
        }
        catch (OperationCanceledException failure) when (!cancellationToken.IsCancellationRequested && timeout > TimeSpan.Zero)
        {
            socket.Dispose();
            throw new ResponsesWebSocketTransportException(
                $"provider: websocket upgrade did not return headers within {timeout}", failure);
        }
        catch (WebSocketException failure) when (socket.HttpStatusCode is { } statusCode)
        {
            socket.Dispose();
            throw new ResponsesWebSocketUpgradeException((int)statusCode, failure.Message, failure);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}

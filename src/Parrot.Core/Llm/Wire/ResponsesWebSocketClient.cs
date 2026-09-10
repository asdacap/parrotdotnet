using System.Net.WebSockets;

namespace Parrot.Llm.Wire;

internal sealed class ResponsesWebSocketClient(
    IResponsesWebSocketConnector connector,
    Uri endpoint,
    TimeSpan connectTimeout,
    TimeSpan idleTimeout,
    int maximumRequestBytes)
{
    private const string BetaHeaderValue = "responses_websockets=2026-02-06";

    public static Uri Endpoint(Uri httpEndpoint)
    {
        ArgumentNullException.ThrowIfNull(httpEndpoint);
        var scheme = httpEndpoint.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            _ => throw new ProviderHttpException("provider: websocket endpoint must derive from HTTP or HTTPS"),
        };
        return new UriBuilder(httpEndpoint) { Scheme = scheme }.Uri;
    }

    public async Task<ResponsesWebSocket> Connect(
        IReadOnlyDictionary<string, string> headers,
        CancellationToken cancellationToken)
    {
        var websocketHeaders = new Dictionary<string, string>(headers, StringComparer.OrdinalIgnoreCase)
        {
            ["OpenAI-Beta"] = BetaHeaderValue,
        };

        try
        {
            var (socket, responseHeaders) = await connector.Connect(
                Endpoint(endpoint), websocketHeaders, connectTimeout, cancellationToken).ConfigureAwait(false);
            return new ResponsesWebSocket(socket, responseHeaders, idleTimeout, maximumRequestBytes);
        }
        catch (WebSocketException failure) when (failure.WebSocketErrorCode == WebSocketError.NotAWebSocket)
        {
            throw new ResponsesWebSocketTransportException(failure.Message, failure);
        }
    }
}

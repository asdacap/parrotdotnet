using System.Net.WebSockets;

namespace Parrot.Llm.Wire;

// Opens response-stream sockets and captures the HTTP upgrade response headers.
internal interface IResponsesWebSocketConnector
{
    // The caller owns a successful socket. Positive timeouts bound connection establishment;
    // cancellation or failure releases the socket before propagating the error.
    Task<(WebSocket Socket, IReadOnlyDictionary<string, string> ResponseHeaders)> Connect(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

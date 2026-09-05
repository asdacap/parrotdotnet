using System.Net.WebSockets;

namespace Parrot.Llm.Wire;

internal interface IResponsesWebSocketConnector
{
    Task<WebSocket> Connect(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken cancellationToken);
}

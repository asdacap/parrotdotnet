using System.Net.Security;
using System.Net.WebSockets;

namespace Parrot.Llm.Wire;

internal sealed class ResponsesWebSocketConnector : IResponsesWebSocketConnector
{
    private readonly string _authority = string.Empty;
    private readonly bool _allowInvalidTlsCertificate;

    public ResponsesWebSocketConnector()
    {
    }

    public ResponsesWebSocketConnector(OpenAICompatibleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.AllowInvalidTlsCertificate &&
            Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out var endpoint) &&
            endpoint.Scheme == Uri.UriSchemeHttps && endpoint.Host.Length > 0)
        {
            _authority = endpoint.GetLeftPart(UriPartial.Authority);
            _allowInvalidTlsCertificate = true;
        }
    }

    public async Task<WebSocket> Connect(
        Uri endpoint,
        IReadOnlyDictionary<string, string> headers,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        if (_allowInvalidTlsCertificate)
        {
            socket.Options.RemoteCertificateValidationCallback = (sender, _, _, errors) =>
                errors == SslPolicyErrors.None || HasAuthority(sender, _authority);
        }

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

    internal static bool HasAuthority(object sender, string authority)
    {
        if (sender is not HttpRequestMessage { RequestUri: { } endpoint })
        {
            return false;
        }

        var scheme = endpoint.Scheme switch
        {
            "wss" => Uri.UriSchemeHttps,
            "ws" => Uri.UriSchemeHttp,
            _ => endpoint.Scheme,
        };
        return string.Equals(
            new UriBuilder(endpoint) { Scheme = scheme }.Uri.GetLeftPart(UriPartial.Authority),
            authority,
            StringComparison.OrdinalIgnoreCase);
    }
}

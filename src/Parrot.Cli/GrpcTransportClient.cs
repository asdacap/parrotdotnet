using System.Net.Http.Headers;
using System.Net.Sockets;
using Grpc.Net.Client;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class GrpcTransportClient : IDisposable
{
    private readonly GrpcChannel _channel;
    private readonly HttpClient _httpClient;
    private readonly SocketsHttpHandler _handler;

    private GrpcTransportClient(GrpcChannel channel, HttpClient httpClient, SocketsHttpHandler handler)
    {
        _channel = channel;
        _httpClient = httpClient;
        _handler = handler;
        Client = new(channel);
    }

    public GeneratedParrot.ParrotClient Client { get; }

    public static GrpcTransportClient Connect(TransportAddress address, TransportToken? token)
    {
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsTcp && token is null)
        {
            throw new InvalidOperationException("TCP transport requires an owner-only bearer token file");
        }

        var handler = BuildHandler(address, out var channelAddress);
        var httpClient = new HttpClient(handler, disposeHandler: true);
        if (token is not null)
        {
            httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Bearer);
        }

        var channel = GrpcChannel.ForAddress(
            channelAddress,
            new GrpcChannelOptions
            {
                HttpClient = httpClient,
                DisposeHttpClient = false,
                MaxReceiveMessageSize = GrpcTransportLimits.MessageBytes,
                MaxSendMessageSize = GrpcTransportLimits.MessageBytes,
            });
        return new(channel, httpClient, handler);
    }

    public void Dispose()
    {
        _channel.Dispose();
        _httpClient.Dispose();
        _handler.Dispose();
    }

    private static SocketsHttpHandler BuildHandler(TransportAddress address, out string channelAddress)
    {
        var handler = new SocketsHttpHandler();
        channelAddress = address.Value;
        if (address.Kind != TransportAddressKind.Unix)
        {
            return handler;
        }

        var socketPath = address.UnixPath;
        channelAddress = "http://localhost";
        handler.ConnectCallback = async (_, cancellationToken) =>
        {
            var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken)
                    .ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        };
        return handler;
    }
}

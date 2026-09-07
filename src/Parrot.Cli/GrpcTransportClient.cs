using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Sockets;
using Grpc.Core;
using Grpc.Net.Client;
using Parrot.Diagnostics;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class GrpcTransportClient : IDisposable
{
    private readonly IDiagnosticLog _diagnostics;
    private readonly string _connectionId;
    private readonly GrpcChannel _channel;
    private readonly HttpClient _httpClient;
    private readonly SocketsHttpHandler _handler;

    private GrpcTransportClient(GrpcChannel channel, HttpClient httpClient, SocketsHttpHandler handler, IDiagnosticLog diagnostics, string connectionId)
    {
        _diagnostics = diagnostics;
        _connectionId = connectionId;
        _channel = channel;
        _httpClient = httpClient;
        _handler = handler;
        Client = new(channel);
    }

    public GeneratedParrot.ParrotClient Client { get; }

    public static GrpcTransportClient Connect(TransportAddress address, TransportToken? token, IDiagnosticLog diagnostics)
    {
        var connectionId = FileDiagnosticLog.CreateInstanceId();
        var started = Stopwatch.GetTimestamp();
        diagnostics.Write(new DiagnosticEvent("transport", "channel.open.start", DiagnosticSeverity.Information)
        {
            CorrelationId = connectionId,
        });
        try
        {
            var connection = OpenConnection(address, token, diagnostics, connectionId);
            diagnostics.Write(new DiagnosticEvent("transport", "channel.open.complete", DiagnosticSeverity.Information)
            {
                CorrelationId = connectionId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "success",
            });
            return connection;
        }
        catch (Exception failure)
        {
            diagnostics.Write(new DiagnosticEvent("transport", "channel.open.complete", DiagnosticSeverity.Error)
            {
                CorrelationId = connectionId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "failure",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    public async Task<UserSession> Attach(
        AttachSessionRequest request,
        CancellationToken cancellationToken)
    {
        var attachmentId = FileDiagnosticLog.CreateInstanceId();
        var started = Stopwatch.GetTimestamp();
        _diagnostics.Write(new DiagnosticEvent("transport", "attach.start", DiagnosticSeverity.Information)
        {
            CorrelationId = attachmentId,
        });
        try
        {
            var session = await AttachSession(request, cancellationToken).ConfigureAwait(false);
            _diagnostics.Write(new DiagnosticEvent("transport", "attach.complete", DiagnosticSeverity.Information)
            {
                CorrelationId = attachmentId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "success",
            });
            return session;
        }
        catch (Exception failure)
        {
            var cancelled = failure is OperationCanceledException or RpcException { StatusCode: StatusCode.Cancelled };
            _diagnostics.Write(new DiagnosticEvent(
                "transport", "attach.complete", cancelled ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                CorrelationId = attachmentId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = cancelled ? "cancelled" : "failure",
                ErrorCode = failure is RpcException rpcFailure
                    ? rpcFailure.StatusCode.ToString() : DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    public void Dispose()
    {
        var started = Stopwatch.GetTimestamp();
        _diagnostics.Write(new DiagnosticEvent("transport", "disconnect.start", DiagnosticSeverity.Information)
        {
            CorrelationId = _connectionId,
        });
        try
        {
            _channel.Dispose();
            _httpClient.Dispose();
            _handler.Dispose();
            _diagnostics.Write(new DiagnosticEvent("transport", "disconnect.complete", DiagnosticSeverity.Information)
            {
                CorrelationId = _connectionId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "success",
            });
        }
        catch (Exception failure)
        {
            _diagnostics.Write(new DiagnosticEvent("transport", "disconnect.complete", DiagnosticSeverity.Error)
            {
                CorrelationId = _connectionId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "failure",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    private static GrpcTransportClient OpenConnection(
        TransportAddress address, TransportToken? token, IDiagnosticLog diagnostics, string connectionId)
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
        return new(channel, httpClient, handler, diagnostics, connectionId);
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

    private async Task<UserSession> AttachSession(AttachSessionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                try
                {
                    var session = await Client.AttachSessionAsync(request, cancellationToken: deadline.Token)
                        .ConfigureAwait(false);
                    if (!string.Equals(session.Id, request.UserSessionId, StringComparison.Ordinal))
                    {
                        throw new InvalidOperationException("the transport returned a different user session");
                    }

                    return session;
                }
                catch (RpcException failure) when (failure.StatusCode is StatusCode.Unavailable or StatusCode.NotFound)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(100), deadline.Token).ConfigureAwait(false);
                }
            }
        }
        catch (Exception failure) when (deadline.IsCancellationRequested
            && failure is OperationCanceledException or RpcException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new TimeoutException("unable to attach to the existing user session within 3 seconds", failure);
        }
    }
}

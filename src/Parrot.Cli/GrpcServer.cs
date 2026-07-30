using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class GrpcServer : IAsyncDisposable
{
    private const UnixFileMode ControlDirectoryMode = UnixFileMode.UserRead
        | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly WebApplication _application;
    private readonly string _ownedSocketPath;

    private GrpcServer(WebApplication application, string ownedSocketPath)
    {
        _application = application;
        _ownedSocketPath = ownedSocketPath;
    }

    public IReadOnlyList<string> Addresses =>
        [.. _application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses ?? []];

    public static async Task<GrpcServer> Start(
        GeneratedParrot.ParrotBase service,
        TransportAddress address,
        TransportToken? token,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsTcp && token is null)
        {
            throw new InvalidOperationException("TCP transport requires an owner-only bearer token file");
        }

        var builder = WebApplication.CreateSlimBuilder();
        var socketPath = address.Kind == TransportAddressKind.Unix ? address.UnixPath : string.Empty;
        PrepareControlDirectory(socketPath);
        ConfigureSocketBinding(builder, socketPath);
        _ = builder.WebHost.ConfigureKestrel(options => ConfigureEndpoint(options, address));
        _ = builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = GrpcTransportLimits.MessageBytes;
            options.MaxSendMessageSize = GrpcTransportLimits.MessageBytes;
        });
        _ = builder.Services.AddSingleton(service);

        var application = builder.Build();
        if (token is not null)
        {
            _ = application.Use(async (context, next) =>
            {
                if (!Authenticate(context, token))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                await next(context).ConfigureAwait(false);
            });
        }

        _ = application.MapGrpcService<GeneratedParrot.ParrotBase>();

        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return new(application, socketPath);
        }
        catch (Exception failure)
        {
            await application.DisposeAsync().ConfigureAwait(false);
            throw failure is InvalidOperationException
                ? failure
                : new InvalidOperationException($"cannot start transport {address.Value}: {failure.Message}", failure);
        }
    }

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        await _application.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        return CommandDispatcher.ExitSuccess;
    }

    public async ValueTask DisposeAsync()
    {
        await _application.DisposeAsync().ConfigureAwait(false);
        if (_ownedSocketPath.Length > 0)
        {
            File.Delete(_ownedSocketPath);
        }
    }

    private static bool Authenticate(HttpContext context, TransportToken token)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.Ordinal)
            && token.Matches(authorization[prefix.Length..]);
    }

    private static void ConfigureEndpoint(KestrelServerOptions options, TransportAddress address)
    {
        if (address.Kind == TransportAddressKind.Unix)
        {
            options.ListenUnixSocket(address.UnixPath, listen => listen.Protocols = HttpProtocols.Http2);
            return;
        }

        var uri = address.TcpUri;
        var endpointAddress = ResolveAddress(uri.Host);
        options.Listen(endpointAddress, uri.Port, listen =>
        {
            listen.Protocols = HttpProtocols.Http2;
            if (address.Kind == TransportAddressKind.Https)
            {
                _ = listen.UseHttps();
            }
        });
    }

    private static IPAddress ResolveAddress(string host)
    {
        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return IPAddress.Loopback;
        }

        return IPAddress.TryParse(host, out var parsed)
            ? parsed
            : throw new InvalidOperationException("a server TCP address must use localhost or a numeric IP address");
    }

    private static void ConfigureSocketBinding(WebApplicationBuilder builder, string socketPath)
    {
        if (socketPath.Length == 0)
        {
            return;
        }

        _ = builder.WebHost.UseSockets(options =>
            options.CreateBoundListenSocket = endpoint => BindSocket(endpoint, socketPath));
    }

    private static Socket BindSocket(EndPoint endpoint, string socketPath)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Unix socket transport is unavailable on Windows");
        }

        var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            socket.Bind(endpoint);
        }
        catch (SocketException failure) when (failure.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            socket.Dispose();
            RemoveStaleSocket(endpoint, socketPath);
            socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
            socket.Bind(endpoint);
        }

        File.SetUnixFileMode(socketPath, SocketMode);
        return socket;
    }

    private static void RemoveStaleSocket(EndPoint endpoint, string socketPath)
    {
        RefuseNonSocket(socketPath);
        using var probe = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            probe.Connect(endpoint);
            throw new InvalidOperationException($"transport socket is already active: {socketPath}");
        }
        catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionRefused)
        {
            File.Delete(socketPath);
        }
    }

    private static void RefuseNonSocket(string socketPath)
    {
        var attributes = File.GetAttributes(socketPath);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0)
        {
            throw new InvalidOperationException($"transport path exists and is not a socket: {socketPath}");
        }

        try
        {
            using var file = File.OpenRead(socketPath);
            throw new InvalidOperationException($"transport path exists and is not a socket: {socketPath}");
        }
        catch (IOException)
        {
        }
    }

    private static void PrepareControlDirectory(string socketPath)
    {
        if (socketPath.Length == 0)
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Unix socket transport is unavailable on Windows");
        }

        var directory = Path.GetDirectoryName(socketPath)
            ?? throw new InvalidOperationException("the Unix socket has no parent directory");
        _ = Directory.CreateDirectory(directory, ControlDirectoryMode);
        File.SetUnixFileMode(directory, ControlDirectoryMode);
    }
}

using System.Diagnostics;
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
using Microsoft.Extensions.Logging;
using Parrot.Diagnostics;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class GrpcServer : IAsyncDisposable
{
    private const UnixFileMode ControlDirectoryMode = UnixFileMode.UserRead
        | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private const UnixFileMode SocketMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly IDiagnosticLog _diagnostics;
    private readonly string _hostId;
    private readonly WebApplication _application;
    private readonly string _ownedSocketPath;
    private readonly UnixSocketIdentity? _socketIdentity;
    private readonly bool _local;

    private GrpcServer(
        WebApplication application,
        string ownedSocketPath,
        UnixSocketIdentity? socketIdentity,
        bool local,
        IDiagnosticLog diagnostics,
        string hostId)
    {
        _diagnostics = diagnostics;
        _hostId = hostId;
        _application = application;
        _ownedSocketPath = ownedSocketPath;
        _socketIdentity = socketIdentity;
        _local = local;
    }

    public IReadOnlyList<string> Addresses =>
        [.. _application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses ?? []];

    public static async Task<GrpcServer> Start(
        GeneratedParrot.ParrotBase service,
        TransportAddress address,
        TransportToken? token,
        IDiagnosticLog diagnostics,
        CancellationToken cancellationToken) =>
        await StartTransport(service, address, token, false, diagnostics, cancellationToken).ConfigureAwait(false);

    public static async Task<GrpcServer> StartLocal(
        GeneratedParrot.ParrotBase service,
        string socketPath,
        IDiagnosticLog diagnostics,
        CancellationToken cancellationToken) =>
        await StartTransport(service, TransportAddress.Parse($"unix:{socketPath}"), null, true, diagnostics, cancellationToken)
            .ConfigureAwait(false);

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        _diagnostics.Write(new DiagnosticEvent("transport", "host.run.start", DiagnosticSeverity.Information)
        {
            CorrelationId = _hostId,
        });
        try
        {
            await _application.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
            _diagnostics.Write(new DiagnosticEvent("transport", "host.run.complete", DiagnosticSeverity.Information)
            {
                CorrelationId = _hostId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = cancellationToken.IsCancellationRequested ? "cancelled" : "success",
            });
            return CommandDispatcher.ExitSuccess;
        }
        catch (Exception failure)
        {
            var cancelled = failure is OperationCanceledException;
            _diagnostics.Write(new DiagnosticEvent(
                "transport", "host.run.complete", cancelled ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                CorrelationId = _hostId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = cancelled ? "cancelled" : "failure",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        var started = Stopwatch.GetTimestamp();
        _diagnostics.Write(new DiagnosticEvent("transport", "host.stop.start", DiagnosticSeverity.Information)
        {
            CorrelationId = _hostId,
        });
        try
        {
            await StopTransport().ConfigureAwait(false);
            _diagnostics.Write(new DiagnosticEvent("transport", "host.stop.complete", DiagnosticSeverity.Information)
            {
                CorrelationId = _hostId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "success",
            });
        }
        catch (Exception failure)
        {
            _diagnostics.Write(new DiagnosticEvent("transport", "host.stop.complete", DiagnosticSeverity.Error)
            {
                CorrelationId = _hostId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "failure",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    private static async Task<GrpcServer> StartTransport(
        GeneratedParrot.ParrotBase service,
        TransportAddress address,
        TransportToken? token,
        bool quiet,
        IDiagnosticLog diagnostics,
        CancellationToken cancellationToken)
    {
        var hostId = FileDiagnosticLog.CreateInstanceId();
        var started = Stopwatch.GetTimestamp();
        diagnostics.Write(new DiagnosticEvent("transport", "host.start", DiagnosticSeverity.Information)
        {
            CorrelationId = hostId,
        });
        try
        {
            var server = await BindTransport(service, address, token, quiet, diagnostics, hostId, cancellationToken)
                .ConfigureAwait(false);
            diagnostics.Write(new DiagnosticEvent("transport", "host.ready", DiagnosticSeverity.Information)
            {
                CorrelationId = hostId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = "success",
            });
            return server;
        }
        catch (Exception failure)
        {
            var cancelled = failure is OperationCanceledException;
            diagnostics.Write(new DiagnosticEvent(
                "transport", "host.failure", cancelled ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                CorrelationId = hostId,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                Outcome = cancelled ? "cancelled" : "failure",
                ErrorCode = DiagnosticEvent.ClassifyFailure(failure),
            });
            throw;
        }
    }

    private static async Task<GrpcServer> BindTransport(
        GeneratedParrot.ParrotBase service,
        TransportAddress address,
        TransportToken? token,
        bool quiet,
        IDiagnosticLog diagnostics,
        string hostId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);
        ArgumentNullException.ThrowIfNull(address);

        if (address.IsTcp && token is null)
        {
            throw new InvalidOperationException("TCP transport requires an owner-only bearer token file");
        }

        var builder = WebApplication.CreateSlimBuilder();
        if (quiet)
        {
            _ = builder.Logging.ClearProviders();
        }

        var socketPath = address.Kind == TransportAddressKind.Unix ? address.UnixPath : string.Empty;
        if (socketPath.Length > 0)
        {
            try
            {
                _ = new UnixDomainSocketEndPoint(socketPath);
            }
            catch (ArgumentException failure)
            {
                throw new InvalidOperationException($"cannot bind Unix socket path {socketPath}: {failure.Message}", failure);
            }
        }

        PrepareControlDirectory(socketPath);
        UnixSocketIdentity? socketIdentity = null;
        ConfigureSocketBinding(builder, socketPath, identity => socketIdentity = identity);
        _ = builder.WebHost.ConfigureKestrel(options => ConfigureEndpoint(options, address));
        _ = builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = GrpcTransportLimits.MessageBytes;
            options.MaxSendMessageSize = GrpcTransportLimits.MessageBytes;
            options.Interceptors.Add<UnhandledFailureInterceptor>(diagnostics);
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
            return new(application, socketPath, socketIdentity, quiet, diagnostics, hostId);
        }
        catch (Exception failure)
        {
            await application.DisposeAsync().ConfigureAwait(false);
            socketIdentity?.Remove(socketPath);
            cancellationToken.ThrowIfCancellationRequested();
            throw failure is InvalidOperationException
                ? failure
                : new InvalidOperationException($"cannot start transport {address.Value}: {failure.Message}", failure);
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

    private static void ConfigureSocketBinding(
        WebApplicationBuilder builder, string socketPath, Action<UnixSocketIdentity> bound)
    {
        if (socketPath.Length == 0)
        {
            return;
        }

        _ = builder.WebHost.UseSockets(options =>
            options.CreateBoundListenSocket = endpoint => BindSocket(endpoint, socketPath, bound));
    }

    private static Socket BindSocket(EndPoint endpoint, string socketPath, Action<UnixSocketIdentity> bound)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new InvalidOperationException("Unix socket transport is unavailable on Windows");
        }

        var existing = UnixSocketIdentity.Read(socketPath);
        if (existing is not null)
        {
            using var probe = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Unspecified);
            try
            {
                probe.Connect(endpoint);
                throw new TransportSocketActiveException($"transport socket is already active: {socketPath}");
            }
            catch (SocketException failure) when (failure.SocketErrorCode == SocketError.ConnectionRefused)
            {
                existing.Remove(socketPath);
            }
        }

        var socket = UnixSocketIdentity.Bind(endpoint);
        try
        {
            var identity = UnixSocketIdentity.Read(socketPath)
                ?? throw new InvalidOperationException("the bound Unix socket disappeared");
            bound(identity);
            File.SetUnixFileMode(socketPath, SocketMode);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
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
        for (var ancestor = new DirectoryInfo(directory); ancestor is not null; ancestor = ancestor.Parent)
        {
            if ((ancestor.Attributes & FileAttributes.ReparsePoint) != 0 && ancestor.Exists)
            {
                throw new InvalidOperationException("Unix socket directories cannot be symbolic links");
            }
        }

        _ = Directory.CreateDirectory(directory, ControlDirectoryMode);
        File.SetUnixFileMode(directory, ControlDirectoryMode);
    }

    private async ValueTask StopTransport()
    {
        try
        {
            try
            {
                if (_local)
                {
                    using var stopping = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                    await _application.StopAsync(stopping.Token).ConfigureAwait(false);
                }
            }
            finally
            {
                await _application.DisposeAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _socketIdentity?.Remove(_ownedSocketPath);
        }
    }
}

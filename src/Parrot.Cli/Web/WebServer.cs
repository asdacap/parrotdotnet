using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Parrot.Diagnostics;
using Parrot.Web.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Web;

// Hosts the browser UI and both gRPC services, as gRPC-Web, on one loopback
// HTTP/1.1 port.
internal sealed class WebServer(WebApplication application) : IAsyncDisposable
{
    public IReadOnlyList<string> Addresses =>
        [.. application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses ?? []];

    public static async Task<WebServer> Start(
        GeneratedParrot.ParrotBase service,
        ParrotWeb.ParrotWebBase webService,
        int port,
        IDiagnosticLog diagnostics,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.Logging.ClearProviders();
        _ = builder.WebHost.ConfigureKestrel(options =>
            options.Listen(IPAddress.Loopback, port, listen => listen.Protocols = HttpProtocols.Http1));
        _ = builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = GrpcTransportLimits.MessageBytes;
            options.MaxSendMessageSize = GrpcTransportLimits.MessageBytes;
            options.Interceptors.Add<UnhandledFailureInterceptor>(diagnostics);
        });
        _ = builder.Services.AddSingleton(service);
        _ = builder.Services.AddSingleton(webService);

        var application = builder.Build();
        _ = application.UseGrpcWeb(new GrpcWebOptions { DefaultEnabled = true });
        _ = application.MapGrpcService<GeneratedParrot.ParrotBase>();
        _ = application.MapGrpcService<ParrotWeb.ParrotWebBase>();
        _ = application.MapGet("/{**path}", new WebAssets(typeof(WebServer).Assembly).Serve);

        try
        {
            await application.StartAsync(cancellationToken).ConfigureAwait(false);
            return new(application);
        }
        catch (Exception failure)
        {
            await application.DisposeAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException($"cannot start the web UI on port {port}: {failure.Message}", failure);
        }
    }

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        await application.WaitForShutdownAsync(cancellationToken).ConfigureAwait(false);
        return CommandDispatcher.ExitSuccess;
    }

    public ValueTask DisposeAsync() => application.DisposeAsync();
}

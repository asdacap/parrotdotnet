using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Parrot.Web.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Web;

// Hosts the browser UI and both gRPC services, as gRPC-Web, on one loopback
// HTTP/1.1 port. Pages are public; every call needs the bearer token.
internal sealed class WebServer(WebApplication application) : IAsyncDisposable
{
    public IReadOnlyList<string> Addresses =>
        [.. application.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()?.Addresses ?? []];

    public static async Task<WebServer> Start(
        GeneratedParrot.ParrotBase service,
        ParrotWeb.ParrotWebBase webService,
        int port,
        TransportToken token,
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
        });
        _ = builder.Services.AddSingleton(service);
        _ = builder.Services.AddSingleton(webService);

        var application = builder.Build();
        _ = application.Use(async (context, next) =>
        {
            if (!HttpMethods.IsGet(context.Request.Method) && !Authenticate(context, token))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context).ConfigureAwait(false);
        });
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

    private static bool Authenticate(HttpContext context, TransportToken token)
    {
        var authorization = context.Request.Headers.Authorization.ToString();
        const string prefix = "Bearer ";
        return authorization.StartsWith(prefix, StringComparison.Ordinal)
            && token.Matches(authorization[prefix.Length..]);
    }
}

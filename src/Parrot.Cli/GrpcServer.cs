using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Parrot.Protocol;

namespace Parrot.Cli;

// The optional listener. Hosts the one service on Kestrel over HTTP/2, which is
// what gRPC needs on the wire. Local mode never builds this -- it binds no
// socket (principle 12).
internal static class GrpcServer
{
    public static async Task<int> Run(ParrotService service, int port, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(service);

        var builder = WebApplication.CreateSlimBuilder();
        _ = builder.WebHost.ConfigureKestrel(options =>
            options.ListenAnyIP(port, listen => listen.Protocols = HttpProtocols.Http2));

        _ = builder.Services.AddGrpc(options =>
        {
            options.MaxReceiveMessageSize = GrpcTransportLimits.MessageBytes;
            options.MaxSendMessageSize = GrpcTransportLimits.MessageBytes;
        });
        _ = builder.Services.AddSingleton(service);

        await using var app = builder.Build();
        _ = app.MapGrpcService<ParrotService>();

        await app.RunAsync(cancellationToken).ConfigureAwait(false);
        return CommandDispatcher.ExitSuccess;
    }
}

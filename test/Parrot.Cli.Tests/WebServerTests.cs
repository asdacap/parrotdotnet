using System.Net;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Parrot.Cli.Web;
using Parrot.Protocol;
using Parrot.Web.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class WebServerTests
{
    [Test]
    public async Task Pages_are_public_and_calls_need_the_bearer_token(CancellationToken cancellationToken)
    {
        var token = TransportToken.Generate();
        await using var server = await WebServer.Start(new TestService(), new TestWebService(), 0, token, cancellationToken);
        var address = new Uri(server.Addresses.Single());
        using var http = new HttpClient { BaseAddress = address };
        var asset = typeof(WebServer).Assembly.GetManifestResourceNames()
            .First(static name => name.StartsWith("web/assets/", StringComparison.Ordinal) && name.EndsWith(".js", StringComparison.Ordinal));

        using var index = await http.GetAsync(new Uri("/", UriKind.Relative), cancellationToken);
        using var route = await http.GetAsync(new Uri("/s/some-session", UriKind.Relative), cancellationToken);
        using var script = await http.GetAsync(new Uri("/" + asset["web/".Length..], UriKind.Relative), cancellationToken);
        var authorized = await ListModes(address, token.Bearer, cancellationToken);
        var failure = await Assert.That(async () => await ListModes(address, TransportToken.Generate().Bearer, cancellationToken))
            .Throws<RpcException>();

        _ = await Assert.That(index.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        _ = await Assert.That(await route.Content.ReadAsStringAsync(cancellationToken))
            .IsEqualTo(await index.Content.ReadAsStringAsync(cancellationToken));
        _ = await Assert.That(script.Content.Headers.ContentType?.MediaType).IsEqualTo("text/javascript");
        _ = await Assert.That(authorized.Modes).IsEmpty();
        _ = await Assert.That(failure?.StatusCode).IsEqualTo(StatusCode.Unauthenticated);
    }

    private static async Task<ListModesResponse> ListModes(Uri address, string bearer, CancellationToken cancellationToken)
    {
        using var handler = new HttpClientHandler();
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, handler),
            HttpVersion = HttpVersion.Version11,
        });
        var client = new GeneratedParrot.ParrotClient(channel);
        return await client.ListModesAsync(
            new ListModesRequest(),
            new Metadata { { "Authorization", $"Bearer {bearer}" } },
            cancellationToken: cancellationToken);
    }

    private sealed class TestService : GeneratedParrot.ParrotBase
    {
        public override Task<ListModesResponse> ListModes(ListModesRequest request, ServerCallContext context) =>
            Task.FromResult(new ListModesResponse());
    }

    private sealed class TestWebService : ParrotWeb.ParrotWebBase;
}

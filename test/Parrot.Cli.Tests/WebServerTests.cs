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
    public async Task Serves_pages_and_calls(CancellationToken cancellationToken)
    {
        using var diagnostics = new TransportDiagnosticsFixture();
        await using var server = await WebServer.Start(new TestService(), new TestWebService(), 0, diagnostics.Log, cancellationToken);
        var address = new Uri(server.Addresses.Single());
        using var http = new HttpClient { BaseAddress = address };
        var asset = typeof(WebServer).Assembly.GetManifestResourceNames()
            .First(static name => name.StartsWith("web/assets/", StringComparison.Ordinal) && name.EndsWith(".js", StringComparison.Ordinal));

        using var index = await http.GetAsync(new Uri("/", UriKind.Relative), cancellationToken);
        using var route = await http.GetAsync(new Uri("/s/some-session", UriKind.Relative), cancellationToken);
        using var script = await http.GetAsync(new Uri("/" + asset["web/".Length..], UriKind.Relative), cancellationToken);
        using var handler = new HttpClientHandler();
        using var channel = GrpcChannel.ForAddress(address, new GrpcChannelOptions
        {
            HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, handler),
            HttpVersion = HttpVersion.Version11,
        });
        var client = new GeneratedParrot.ParrotClient(channel);
        var modes = await client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);
        var failure = await Assert.ThrowsAsync<RpcException>(async () =>
            await client.ListSessionsAsync(new ListSessionsRequest(), cancellationToken: cancellationToken));

        _ = await Assert.That(index.Content.Headers.ContentType?.MediaType).IsEqualTo("text/html");
        _ = await Assert.That(await route.Content.ReadAsStringAsync(cancellationToken))
            .IsEqualTo(await index.Content.ReadAsStringAsync(cancellationToken));
        _ = await Assert.That(script.Content.Headers.ContentType?.MediaType).IsEqualTo("text/javascript");
        _ = await Assert.That(modes.Modes).IsEmpty();
        _ = await Assert.That(failure?.StatusCode).IsEqualTo(StatusCode.Internal);
        _ = await Assert.That(failure?.Status.Detail).IsEqualTo("sessions directory is unreadable");
        _ = await Assert.That(diagnostics.Read())
            .Contains("event=\"handler.failure\"")
            .And.Contains("request=\"/parrot.v1.Parrot/ListSessions\"")
            .And.Contains("error=\"io\"");
    }

    private sealed class TestService : GeneratedParrot.ParrotBase
    {
        public override Task<ListModesResponse> ListModes(ListModesRequest request, ServerCallContext context) =>
            Task.FromResult(new ListModesResponse());

        public override Task<ListSessionsResponse> ListSessions(ListSessionsRequest request, ServerCallContext context) =>
            throw new IOException("sessions directory is unreadable");
    }

    private sealed class TestWebService : ParrotWeb.ParrotWebBase;
}

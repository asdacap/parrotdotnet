using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Security;
using Parrot.Tools;
using Parrot.Web;

namespace Parrot.Core.Tests;

internal sealed class WebFetcherTests
{
    [Test]
    public async Task Local_fetches_require_opt_in_and_revalidate_redirects(CancellationToken cancellationToken)
    {
        var listener = StartListener(out var baseAddress);
        var serveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var serving = Serve(
            listener,
            [
                Response("302 Found", "text/plain", [], "Location: /html\r\n"),
                Response(
                    "200 OK",
                    "text/html; charset=utf-8",
                    Encoding.UTF8.GetBytes(
                        "<html><style>secret</style><body><h1>Hello &amp; bye</h1>"
                        + "<script>alert(1)</script><p>Text\u0001</p></body></html>"),
                    string.Empty),
                Response("200 OK", "text/plain", Gzip(new string('a', 300)), "Content-Encoding: gzip\r\n"),
                Response("200 OK", "text/plain", Encoding.UTF8.GetBytes("fetched"), string.Empty),
            ],
            serveCts.Token);
        var privateFetcher = new WebFetcher(
            new PrivateWebAddressPolicy(), 5, 256, TimeSpan.FromSeconds(5), "test-agent");

        try
        {
            _ = await Assert.That(async () =>
                    await WebFetcher.Create(new PublicWebAddressPolicy()).Fetch(
                        new Uri(baseAddress, "/blocked"), HttpMethod.Get, cancellationToken))
                .Throws<WebFetchException>();

            var html = await privateFetcher.Fetch(
                new Uri(baseAddress, "/start#fragment"), HttpMethod.Get, cancellationToken);
            var bounded = await privateFetcher.Fetch(
                new Uri(baseAddress, "/gzip"), HttpMethod.Get, cancellationToken);
            ITool tool = new WebFetchTool(privateFetcher);
            var toolText = (await tool.Execute(
                new ToolInvocation(
                    "test-call",
                    $$"""{"url":"{{new Uri(baseAddress, "/tool")}}","method":"get"}"""),
                new SelectionFixture().Selection,
                cancellationToken)).Text;
            var requests = await serving;

            _ = await Assert.That(html.FinalAddress).IsEqualTo(new Uri(baseAddress, "/html"));
            _ = await Assert.That(html.StatusCode).IsEqualTo(200);
            _ = await Assert.That(html.ContentType).IsEqualTo("text/html");
            _ = await Assert.That(html.Text).IsEqualTo("Hello & bye\n\nText");
            _ = await Assert.That(html.Truncated).IsFalse();
            _ = await Assert.That(bounded.Text).IsEqualTo(new string('a', 256));
            _ = await Assert.That(bounded.Truncated).IsTrue();
            _ = await Assert.That(toolText).IsEqualTo("fetched");
            _ = await Assert.That(requests[0]).StartsWith("GET /start HTTP/");
            _ = await Assert.That(requests[1]).StartsWith("GET /html HTTP/");
            _ = await Assert.That(requests[2]).StartsWith("GET /gzip HTTP/");
            _ = await Assert.That(requests[2]).Contains("Accept-Encoding: gzip");
            _ = await Assert.That(requests[3]).StartsWith("GET /tool HTTP/");
        }
        finally
        {
            await serveCts.CancelAsync();

            try
            {
                _ = await serving;
            }
            catch (OperationCanceledException)
            {
            }

            serveCts.Dispose();
            listener.Dispose();
        }
    }

    [Test]
    public async Task Redirects_to_forbidden_schemes_are_rejected(CancellationToken cancellationToken)
    {
        var listener = StartListener(out var baseAddress);
        var serveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var serving = Serve(
            listener,
            [Response("302 Found", "text/plain", [], "Location: file:///etc/passwd\r\n")],
            serveCts.Token);
        var fetcher = WebFetcher.Create(new PrivateWebAddressPolicy());

        try
        {
            _ = await Assert.That(async () =>
                    await fetcher.Fetch(baseAddress, HttpMethod.Get, cancellationToken))
                .Throws<WebFetchException>();
            _ = await serving;
        }
        finally
        {
            await serveCts.CancelAsync();

            try
            {
                _ = await serving;
            }
            catch (OperationCanceledException)
            {
            }

            serveCts.Dispose();
            listener.Dispose();
        }
    }

    [Test]
    public async Task Caller_cancellation_is_propagated()
    {
        var listener = StartListener(out var baseAddress);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        var serving = HoldResponse(listener, cancellation.Token);
        var fetcher = new WebFetcher(
            new PrivateWebAddressPolicy(), 5, 64, TimeSpan.FromSeconds(5), "test-agent");

        try
        {
            _ = await Assert.That(async () =>
                    await fetcher.Fetch(baseAddress, HttpMethod.Get, cancellation.Token))
                .Throws<OperationCanceledException>();
            await serving;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            listener.Dispose();
        }
    }

    [Test]
    public async Task Request_normalization_accepts_only_get_and_head()
    {
        var (address, defaultMethod) = WebFetchTool.ReadRequest("{\"url\":\"example.com/path#part\"}");
        var (_, headMethod) = WebFetchTool.ReadRequest("{\"url\":\"https://example.com\",\"method\":\" head \"}");
        ITool tool = new WebFetchTool(WebFetcher.Create(new PublicWebAddressPolicy()));

        _ = await Assert.That(address).IsEqualTo(new Uri("https://example.com/path"));
        _ = await Assert.That(defaultMethod).IsEqualTo(HttpMethod.Get);
        _ = await Assert.That(headMethod).IsEqualTo(HttpMethod.Head);
        _ = await Assert.That((await tool.Execute(new ToolInvocation("test-call", "[]"), new SelectionFixture().Selection, CancellationToken.None)).Text).Contains("URL is required");
        _ = await Assert.That((await tool.Execute(
            new ToolInvocation(
                "test-call",
                "{\"url\":\"https://example.com\",\"method\":\"POST\"}"),
            new SelectionFixture().Selection,
            CancellationToken.None)).Text)
            .Contains("only GET and HEAD");
    }

    [Test]
    public async Task Malformed_html_is_sanitized() =>
        _ = await Assert.That(HtmlText.Extract(
                "<h1>Title</h1><!-- comment > still comment --><p>A&nbsp; B</p>"
                + "<script>bad</script><div title=\">\">C</div>"))
            .IsEqualTo("Title\n\nA B\n\nC");

    private static byte[] Gzip(string text)
    {
        using var result = new MemoryStream();

        using (var compressor = new GZipStream(result, CompressionLevel.Fastest, leaveOpen: true))
        {
            compressor.Write(Encoding.UTF8.GetBytes(text));
        }

        return result.ToArray();
    }

    private static byte[] Response(string status, string contentType, byte[] body, string headers)
    {
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n{headers}"
            + $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n");
        var response = new byte[head.Length + body.Length];
        head.CopyTo(response, 0);
        body.CopyTo(response, head.Length);
        return response;
    }

    private static async Task<string[]> Serve(
        TcpListener listener,
        byte[][] responses,
        CancellationToken cancellationToken)
    {
        var requests = new List<string>(responses.Length);

        foreach (var response in responses)
        {
            using var client = await listener.AcceptTcpClientAsync(cancellationToken);
            await using var stream = client.GetStream();
            requests.Add(await ReadRequest(stream, cancellationToken));
            await stream.WriteAsync(response, cancellationToken);
        }

        return [.. requests];
    }

    private static async Task HoldResponse(TcpListener listener, CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        _ = await ReadRequest(stream, cancellationToken);
        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
    }

    private static async Task<string> ReadRequest(Stream stream, CancellationToken cancellationToken)
    {
        using var request = new MemoryStream();
        var buffer = new byte[1024];

        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);

            if (read == 0)
            {
                break;
            }

            await request.WriteAsync(buffer.AsMemory(0, read), cancellationToken);

            if (request.Length >= 4
                && request.GetBuffer().AsSpan(0, (int)request.Length).EndsWith("\r\n\r\n"u8))
            {
                break;
            }
        }

        return Encoding.ASCII.GetString(request.GetBuffer(), 0, (int)request.Length);
    }

    private static TcpListener StartListener(out Uri address)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        address = new Uri($"http://127.0.0.1:{endpoint.Port}");
        return listener;
    }

    private sealed class SelectionFixture
    {
        public SelectionFixture()
        {
            ILLMProvider provider = new UnusedProvider();
            var model = new ProviderModel(provider, new LLMModel("model", provider.Id));
            Selection = new AgentTurnSelection(
                new ModelSelector(model.Selector),
                TestModels.Resolve(model),
                new TestProfileFixture().Mode,
                SecurityProfile.Compose(readOnly: false, [], [], []));
        }

        public AgentTurnSelection Selection { get; }
    }
}

using System.Net;
using System.Text;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class HttpResponseByteTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Bounded_reads_count_bytes_including_limit_probe(
        bool asynchronous,
        bool exceedsLimit,
        CancellationToken cancellationToken)
    {
        var attempt = new ProviderAttemptDiagnostics(
            TestDiagnosticLog.Instance, new DiagnosticEvent("provider", "attempt", DiagnosticSeverity.Information));
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(exceedsLimit ? "éé!" : "éé"));
        using var owner = new MemoryStream();
        await using var bounded = new BoundedStream(source, 4, owner) { Attempt = attempt };
        var buffer = new byte[8];
        async Task Read()
        {
            var count = asynchronous
                ? await bounded.ReadAsync(buffer.AsMemory(), cancellationToken)
                : ReadSynchronously(bounded, buffer);
            _ = await Assert.That(count).IsEqualTo(4);
        }

        if (exceedsLimit)
        {
            _ = await Assert.That(Read).Throws<WireProtocolException>();
        }
        else
        {
            await Read();
        }

        _ = await Assert.That(attempt.ResponseBytes).IsEqualTo(exceedsLimit ? 5L : 4L);
        _ = await Assert.That(source.Position).IsEqualTo(exceedsLimit ? 5L : 4L);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task Http_counts_consumed_body_without_draining_and_bounds_errors(
        bool error,
        bool oversized,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(oversized ? new string('é', HttpStreaming.MaxErrorBytes) : "éé!");
        using var handler = new BodyHandler(error, body);
        using var client = new HttpClient(handler, disposeHandler: false);
        var attempt = new ProviderAttemptDiagnostics(
            TestDiagnosticLog.Instance, new DiagnosticEvent("provider", "attempt", DiagnosticSeverity.Information));

        async Task Send()
        {
            await using var response = await HttpStreaming.OpenStream(
                client,
                new Uri("https://example.test/responses"),
                [],
                new Dictionary<string, string>(StringComparer.Ordinal),
                TimeSpan.FromSeconds(1),
                10,
                attempt,
                cancellationToken);
            _ = await Assert.That(attempt.ResponseBytes).IsEqualTo(0L);
            var buffer = new byte[2];
            _ = await response.Content.ReadAsync(buffer.AsMemory(), cancellationToken);
            _ = await Assert.That(attempt.ResponseBytes).IsEqualTo(2L);
        }

        if (error)
        {
            _ = await Assert.That(Send).Throws<ProviderHttpException>();
        }
        else
        {
            await Send();
        }

        var expected = error ? oversized ? HttpStreaming.MaxErrorBytes + 4L : body.Length : 2L;
        _ = await Assert.That(attempt.ResponseBytes).IsEqualTo(expected);
    }

    private static int ReadSynchronously(Stream stream, byte[] buffer) => stream.Read(buffer, 0, buffer.Length);

    private sealed class BodyHandler(bool error, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(error ? HttpStatusCode.BadRequest : HttpStatusCode.OK)
            {
                Content = new StreamContent(new MemoryStream(body)),
            });
    }
}

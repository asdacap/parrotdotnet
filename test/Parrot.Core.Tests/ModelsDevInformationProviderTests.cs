using System.Net;
using System.Text;
using Parrot.Llm;

namespace Parrot.Core.Tests;

internal sealed class ModelsDevInformationProviderTests
{
    [Test]
    public async Task Fetch_uses_fixed_unauthenticated_endpoint_and_decodes_catalogue(
        CancellationToken cancellationToken)
    {
        using var handler = new RecordingHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                    """
                    {"openai":{"id":"openai","models":{"external":{"id":"external-model","name":"External"}}}}
                    """,
                    Encoding.UTF8,
                    "application/json"),
        }));
        using var client = new HttpClient(handler, disposeHandler: false);

        var catalogue = await new ModelsDevInformationProvider(client).Fetch(cancellationToken);

        _ = await Assert.That(handler.Requests).IsEqualTo(1);
        _ = await Assert.That(handler.Uri).IsEqualTo(new Uri("https://models.dev/api.json"));
        _ = await Assert.That(handler.Authorization).IsNull();
        _ = await Assert.That(handler.ProviderHeader).IsNull();
        _ = await Assert.That(catalogue["openai"].Single().Id).IsEqualTo("external-model");
    }

    [Test]
    [Arguments("http")]
    [Arguments("json")]
    [Arguments("schema")]
    [Arguments("oversized")]
    [Arguments("transport")]
    public async Task Fetch_treats_expected_source_failures_as_an_empty_catalogue(string failure)
    {
        using var handler = new RecordingHandler((_, _) => failure switch
        {
            "http" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)),
            "json" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not-json", Encoding.UTF8, "application/json"),
            }),
            "schema" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("[]", Encoding.UTF8, "application/json"),
            }),
            "oversized" => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(new byte[(16 << 20) + 1]),
            }),
            _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("unavailable")),
        });
        using var client = new HttpClient(handler, disposeHandler: false);

        var catalogue = await new ModelsDevInformationProvider(client).Fetch(CancellationToken.None);

        _ = await Assert.That(catalogue).IsEmpty();
    }

    [Test]
    public async Task Fetch_propagates_caller_cancellation()
    {
        using var handler = new RecordingHandler(static (_, cancellationToken) =>
            Task.FromCanceled<HttpResponseMessage>(cancellationToken));
        using var client = new HttpClient(handler, disposeHandler: false);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        _ = await Assert.That(async () =>
            await new ModelsDevInformationProvider(client).Fetch(cancellation.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    [Timeout(10_000)]
    public async Task Fetch_times_out_while_reading_the_response_body(CancellationToken cancellationToken)
    {
        using var handler = new RecordingHandler(static (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new StalledStream()),
        }));
        using var client = new HttpClient(handler, disposeHandler: false);

        var started = DateTime.UtcNow;
        var catalogue = await new ModelsDevInformationProvider(client).Fetch(cancellationToken);

        _ = await Assert.That(catalogue).IsEmpty();
        _ = await Assert.That(DateTime.UtcNow - started).IsLessThan(TimeSpan.FromSeconds(10));
    }

    private sealed class StalledStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken) =>
            AwaitCancellation(cancellationToken);

        private static async ValueTask<int> AwaitCancellation(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        public Uri? Uri { get; private set; }

        public string? Authorization { get; private set; }

        public string? ProviderHeader { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests++;
            Uri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            ProviderHeader = request.Headers.TryGetValues("X-Provider", out var values) ? values.Single() : null;
            return respond(request, cancellationToken);
        }
    }
}

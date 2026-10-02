using System.Net;
using System.Security.Cryptography;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Llm.ImageUpload;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ImageLinkingProviderTests
{
    private static readonly string AccessKeyEnv = SetVariable("PARROT_TEST_LINK_ACCESS_KEY", "ak");
    private static readonly string SecretKeyEnv = SetVariable("PARROT_TEST_LINK_SECRET_KEY", "sk");
    private static readonly byte[] Image = Convert.FromHexString("000102");
    private static readonly string Url = $"https://minio.example.com/b/parrot/{Convert.ToHexStringLower(SHA256.HashData(Image))}.png";

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Images_are_linked_and_uploaded_once_across_calls_and_sessions(bool useSession, CancellationToken cancellationToken)
    {
        var inner = new ScriptedProvider(Completed, Completed, Completed);
        using var handler = new RecordingHttpHandler(static _ => Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = new ImageLinkingProvider(inner, Bucket(client));
        var request = Request();
        var textOnly = new LLMRequest { Model = "m", Messages = [LLMMessage.User("hello")] };

        _ = await Consume(provider, useSession, request, cancellationToken);
        _ = await Consume(provider, !useSession, request, cancellationToken);
        _ = await Consume(provider, useSession, textOnly, cancellationToken);

        var contents = inner.Requests[0].Messages[0].Contents;
        _ = await Assert.That(contents.Select(content => (content.Kind, content.Text, content.ImageUrl, content.ImagePath, content.ImageWidth)).ToList()).IsEquivalentTo(
        [
            (LLMContentKind.Text, "before", string.Empty, string.Empty, 0),
            (LLMContentKind.Image, string.Empty, Url, request.Messages[0].Contents[1].ImagePath, 2),
            (LLMContentKind.Text, "after", string.Empty, string.Empty, 0),
        ]);
        _ = await Assert.That(inner.Requests[1].Messages[0].Contents[1].ImageUrl).IsEqualTo(Url);
        _ = await Assert.That(ReferenceEquals(inner.Requests[2], textOnly)).IsTrue();
        _ = await Assert.That(handler.Requests.Select(static request => $"{request.Method} {request.Uri.PathAndQuery}").ToList())
            .IsEquivalentTo(["HEAD /b", "PUT /b?policy", $"PUT {new Uri(Url).AbsolutePath}"]);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_missing_object_is_reuploaded_and_the_call_retried_once(bool useSession, CancellationToken cancellationToken)
    {
        var inner = new ScriptedProvider(() => Throw(new ProviderHttpException(400, "invalid_request_error", string.Empty, "could not download")), Completed);
        using var handler = new RecordingHttpHandler(request => Respond(
            request.Method == "HEAD" && request.Uri.AbsolutePath.EndsWith(".png", StringComparison.Ordinal) ? HttpStatusCode.NotFound : HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = new ImageLinkingProvider(inner, Bucket(client));

        var events = await Consume(provider, useSession, Request(), cancellationToken);

        _ = await Assert.That(events.Select(static published => published.Kind).ToList()).IsEquivalentTo([LLMEventKind.Retry, LLMEventKind.Completed]);
        _ = await Assert.That(events[0].Text).IsEqualTo("Re-uploaded missing image objects. Retrying.");
        _ = await Assert.That(inner.Requests.Select(request => request.Messages[0].Contents[1].ImageUrl).ToList()).IsEquivalentTo([Url, Url]);
        _ = await Assert.That(handler.Count("HEAD", "/b")).IsEqualTo(2);
        _ = await Assert.That(handler.Count("PUT", new Uri(Url).AbsolutePath)).IsEqualTo(2);
        _ = await Assert.That(handler.Count("HEAD", new Uri(Url).AbsolutePath)).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Rejected_urls_fall_back_to_inline_bytes_for_the_rest_of_the_process(bool useSession, CancellationToken cancellationToken)
    {
        var inner = new ScriptedProvider(() => Throw(new ProviderHttpException(400, "invalid_request_error", string.Empty, "bad image")), Completed, Completed);
        using var handler = new RecordingHttpHandler(static _ => Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = new ImageLinkingProvider(inner, Bucket(client));
        var request = Request();

        var events = await Consume(provider, useSession, request, cancellationToken);
        var requestsAfterFallback = handler.Requests.Count;
        _ = await Consume(provider, !useSession, request, cancellationToken);

        _ = await Assert.That(events.Select(static published => published.Kind).ToList()).IsEquivalentTo([LLMEventKind.Retry, LLMEventKind.Completed]);
        _ = await Assert.That(events[0].Text).IsEqualTo("Provider rejected image URLs. Retrying with inline image data.");
        _ = await Assert.That(inner.Requests[0].Messages[0].Contents[1].ImageUrl).IsEqualTo(Url);
        _ = await Assert.That(ReferenceEquals(inner.Requests[1], request) && ReferenceEquals(inner.Requests[2], request)).IsTrue();
        _ = await Assert.That(handler.Count("HEAD", new Uri(Url).AbsolutePath)).IsEqualTo(1);
        _ = await Assert.That(handler.Requests.Count).IsEqualTo(requestsAfterFallback);
    }

    [Test]
    [Arguments("visible", 0)]
    [Arguments("timeout", 0)]
    [Arguments("permanent", 0)]
    [Arguments("overload", 1)]
    [Arguments("usage", 1)]
    [Arguments("unauthorized", 1)]
    public async Task Other_failures_are_rethrown_after_at_most_a_verification(string kind, int verifications, CancellationToken cancellationToken)
    {
        Exception failure = kind switch
        {
            "timeout" => new HeaderTimeoutException("timed out"),
            "permanent" => new LLMProviderException("no key"),
            "overload" => new ProviderHttpException(503, string.Empty, string.Empty, "busy"),
            "usage" => new ProviderResponseException("usage_limit_reached", string.Empty, "limit"),
            "unauthorized" => new ProviderHttpException(401, string.Empty, string.Empty, "no"),
            _ => new ProviderHttpException(400, string.Empty, string.Empty, "late"),
        };
        var inner = new ScriptedProvider(() => kind == "visible" ? Throw(failure, LLMEvent.TextDelta("partial")) : Throw(failure));
        using var handler = new RecordingHttpHandler(static _ => Respond(HttpStatusCode.OK));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = new ImageLinkingProvider(inner, Bucket(client));

        var thrown = await Assert.ThrowsAsync<Exception>(() => Consume(provider, false, Request(), cancellationToken));

        _ = await Assert.That(ReferenceEquals(thrown, failure)).IsTrue();
        _ = await Assert.That(inner.Requests.Count).IsEqualTo(1);
        _ = await Assert.That(handler.Count("HEAD", new Uri(Url).AbsolutePath)).IsEqualTo(verifications);
    }

    private static async Task<List<LLMEvent>> Consume(ILLMProvider provider, bool useSession, LLMRequest request, CancellationToken cancellationToken)
    {
        var received = new List<LLMEvent>();
        await using var session = provider.OpenSession();
        await foreach (var published in useSession ? session.Call(request, cancellationToken) : provider.Call(request, cancellationToken))
        {
            received.Add(published);
        }

        return received;
    }

    private static LLMRequest Request()
    {
        var path = Path.Combine(Path.GetTempPath(), $"parrot-link-{Guid.NewGuid():n}.png");
        File.WriteAllBytes(path, Image);
        return new LLMRequest
        {
            Model = "m",
            Messages = [LLMMessage.User([LLMContent.TextPart("before"), LLMContent.ImageFile(path, "image/png", 2, 1), LLMContent.TextPart("after")])],
        };
    }

    private static S3ImageBucket Bucket(HttpClient client) =>
        new(
            client,
            new ImageUploadConfig
            {
                Endpoint = "https://minio.example.com",
                Bucket = "b",
                AccessKeyEnv = AccessKeyEnv,
                SecretKeyEnv = SecretKeyEnv,
            },
            new ImmediateTimeProvider(),
            new InMemoryCredentialStore());

    private static HttpResponseMessage Respond(HttpStatusCode status) => new(status) { Content = new StringContent(string.Empty) };

    private static IAsyncEnumerable<LLMEvent> Completed() => Yield(LLMEvent.Completed("stop", 1, 0, 1, "answer", []));

    private static async IAsyncEnumerable<LLMEvent> Yield(LLMEvent published)
    {
        await Task.Yield();
        yield return published;
    }

    private static async IAsyncEnumerable<LLMEvent> Throw(Exception failure, params LLMEvent[] events)
    {
        foreach (var published in events)
        {
            await Task.Yield();
            yield return published;
        }

        throw failure;
    }

    private static string SetVariable(string name, string value)
    {
        Environment.SetEnvironmentVariable(name, value);
        return name;
    }

    private sealed class ScriptedProvider(params Func<IAsyncEnumerable<LLMEvent>>[] attempts) : ILLMProvider
    {
        private readonly Queue<Func<IAsyncEnumerable<LLMEvent>>> _attempts = new(attempts);

        public List<LLMRequest> Requests { get; } = [];

        public string Id => "scripted";

        public IReadOnlyList<LLMModel> SeedModels() => [];

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<IReadOnlyList<LLMModel>> ListModels(CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<LLMEvent> Call(LLMRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return _attempts.Dequeue()();
        }
    }
}

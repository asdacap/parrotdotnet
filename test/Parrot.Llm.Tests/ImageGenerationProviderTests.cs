using System.Net;
using System.Text;
using System.Text.Json;
using Parrot.Auth;
using Parrot.Llm;
using Parrot.Llm.Wire;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace Parrot.Core.Tests;

internal sealed class ImageGenerationProviderTests
{
    private const string PngBase64 = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGP4z8DwHwAFAAH/iZk9HQAAAABJRU5ErkJggg==";
    private const string SuccessBody = "{\"data\":[{\"b64_json\":\"" + PngBase64 + "\"}]}";

    [Test]
    [Arguments("compatible", "https://example.test/v1", false)]
    [Arguments("compatible", "https://example.test/custom/v1/", true)]
    [Arguments("chatgpt", "https://example.test/unused", false)]
    [Arguments("chatgpt", "https://example.test/unused", true)]
    [Arguments("retry", "https://example.test/v1", true)]
    [Arguments("kimi", "https://example.test/v1", true)]
    [Arguments("opencode", "https://example.test/v1", true)]
    public async Task Generates_and_edits_with_fresh_credentials_and_ordered_json(
        string providerKind, string baseUrl, bool edit, CancellationToken cancellationToken)
    {
        using var fixture = new ProviderFixture(providerKind, baseUrl, SuccessBody, HttpStatusCode.OK);
        using var referenceImage = new Image<Rgba32>(1, 1);
        using var referenceStream = new MemoryStream();
        await referenceImage.SaveAsJpegAsync(referenceStream, cancellationToken);
        ImageGenerationReference[] references = edit
            ? [new("image/png", Convert.FromBase64String(PngBase64)), new("image/jpeg", referenceStream.ToArray())]
            : [];
        var request = new ImageGenerationRequest("  Keep this prompt verbatim.\nSecond line.  ", references);

        for (var operation = 1; operation <= 2; operation++)
        {
            var result = await fixture.Provider.GenerateImage(request, cancellationToken);
            _ = await Assert.That(Convert.ToBase64String(result.Data)).IsEqualTo(PngBase64);
            var captured = fixture.Handler.Requests[operation - 1];
            var endpoint = providerKind == "chatgpt" ? "https://chatgpt.com/backend-api/codex" : baseUrl.TrimEnd('/');
            _ = await Assert.That(captured.Url).IsEqualTo(endpoint + (edit ? "/images/edits" : "/images/generations"));
            _ = await Assert.That(captured.Method).IsEqualTo(HttpMethod.Post);
            _ = await Assert.That(captured.ContentType).IsEqualTo("application/json");
            _ = await Assert.That(captured.Headers["Authorization"]).IsEqualTo($"Bearer key-{operation}");
            if (providerKind == "chatgpt")
            {
                _ = await Assert.That(captured.Headers["ChatGPT-Account-Id"]).IsEqualTo($"account-{operation}");
                _ = await Assert.That(captured.Headers["originator"]).IsEqualTo("parrot");
                _ = await Assert.That(captured.Headers["User-Agent"]).IsEqualTo("parrot");
            }
            else
            {
                _ = await Assert.That(captured.Headers["X-Tenant"]).IsEqualTo("tenant");
            }

            using var body = JsonDocument.Parse(captured.Body);
            var root = body.RootElement;
            _ = await Assert.That(root.GetProperty("prompt").GetString()).IsEqualTo(request.Prompt);
            _ = await Assert.That(root.GetProperty("model").GetString()).IsEqualTo("gpt-image-2");
            foreach (var property in new[] { "background", "quality", "size" })
            {
                _ = await Assert.That(root.GetProperty(property).GetString()).IsEqualTo("auto");
            }

            _ = await Assert.That(root.TryGetProperty("stream", out _)).IsFalse();
            _ = await Assert.That(root.TryGetProperty("images", out var images)).IsEqualTo(edit);
            if (edit)
            {
                _ = await Assert.That(images.GetArrayLength()).IsEqualTo(2);
                for (var index = 0; index < references.Length; index++)
                {
                    _ = await Assert.That(images[index].GetProperty("image_url").GetString())
                        .IsEqualTo($"data:{references[index].MediaType};base64,{Convert.ToBase64String(references[index].Data)}");
                }
            }
        }

        _ = await Assert.That(fixture.Credentials.RequestCount).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Stored_keys_and_environment_precedence_are_resolved_on_each_image_operation(
        bool environmentOverride, CancellationToken cancellationToken)
    {
        var environmentName = "PARROT_IMAGE_TEST_" + Guid.NewGuid().ToString("N");
        var store = new ImageCredentialStore();
        using var handler = new RecordingHandler(SuccessBody, HttpStatusCode.OK);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                ApiKeySource = new StoredApiKeySource("configured", environmentName, store),
            },
            client);
        try
        {
            for (var operation = 1; operation <= 2; operation++)
            {
                await store.Set("configured", Credential.ForApiKey($"stored-{operation}"), cancellationToken);
                if (environmentOverride)
                {
                    Environment.SetEnvironmentVariable(environmentName, $"environment-{operation}");
                }

                _ = await provider.GenerateImage(new("prompt", []), cancellationToken);
                _ = await Assert.That(handler.Requests[operation - 1].Headers["Authorization"])
                    .IsEqualTo($"Bearer {(environmentOverride ? "environment" : "stored")}-{operation}");
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable(environmentName, null);
        }
    }

    [Test]
    [Arguments("chatgpt", HttpStatusCode.Unauthorized)]
    [Arguments("retry", HttpStatusCode.TooManyRequests)]
    [Arguments("kimi", HttpStatusCode.NotFound)]
    [Arguments("opencode", HttpStatusCode.ServiceUnavailable)]
    public async Task Paid_requests_are_not_retried_and_failure_bodies_are_redacted(
        string providerKind, HttpStatusCode statusCode, CancellationToken cancellationToken)
    {
        const string sensitiveBody = "request secret key-1 data:image/png;base64," + PngBase64;
        using var fixture = new ProviderFixture(providerKind, "https://example.test/v1", sensitiveBody, statusCode);
        LLMProviderException? failure = null;
        try
        {
            _ = await fixture.Provider.GenerateImage(new("prompt", []), cancellationToken);
        }
        catch (LLMProviderException caught)
        {
            failure = caught;
        }

        _ = await Assert.That(failure).IsNotNull();
        _ = await Assert.That(failure?.Message).IsEqualTo($"provider: image generation failed (HTTP {(int)statusCode})");
        _ = await Assert.That(failure?.InnerException).IsNull();
        _ = await Assert.That(fixture.Handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("not JSON")]
    [Arguments("null")]
    [Arguments("{}")]
    [Arguments("{\"data\":[]}")]
    [Arguments("{\"data\":[null]}")]
    [Arguments("{\"data\":[{}]}")]
    [Arguments("{\"data\":[{\"b64_json\":\"\"}]}")]
    [Arguments("{\"data\":[{\"b64_json\":\"%%%\"}]}")]
    [Arguments("{\"data\":[{\"b64_json\":\"aGVsbG8=\"}]}")]
    [Arguments("{\"data\":[{\"url\":\"https://example.test/not-downloaded.png\"}]}")]
    [Arguments("{\"data\":[{\"b64_json\":\"" + PngBase64 + "\"},{\"b64_json\":\"" + PngBase64 + "\"}]}")]
    public async Task Invalid_or_ambiguous_responses_fail_without_followup_requests(
        string responseBody, CancellationToken cancellationToken)
    {
        using var fixture = new ProviderFixture("retry", "https://example.test/v1", responseBody, HttpStatusCode.OK);
        _ = await Assert.That(async () => await fixture.Provider.GenerateImage(new("prompt", []), cancellationToken))
            .Throws<LLMProviderException>();
        _ = await Assert.That(fixture.Handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("empty-prompt")]
    [Arguments("six-references")]
    [Arguments("oversized-reference")]
    [Arguments("empty-reference")]
    [Arguments("wrong-media-type")]
    public async Task Invalid_requests_fail_before_credentials_and_http(string invalidCase, CancellationToken cancellationToken)
    {
        using var fixture = new ProviderFixture("compatible", "https://example.test/v1", SuccessBody, HttpStatusCode.OK);
        var reference = new ImageGenerationReference("image/png", Convert.FromBase64String(PngBase64));
        var request = invalidCase switch
        {
            "empty-prompt" => new ImageGenerationRequest(" \n", []),
            "six-references" => new("prompt", [.. Enumerable.Repeat(reference, 6)]),
            "oversized-reference" => new("prompt", [new("image/png", new byte[(10 << 20) + 1])]),
            "empty-reference" => new("prompt", [new("image/png", [])]),
            "wrong-media-type" => new("prompt", [reference with { MediaType = "image/jpeg" }]),
            _ => throw new ArgumentException("Unknown case", nameof(invalidCase)),
        };
        _ = await Assert.That(async () => await fixture.Provider.GenerateImage(request, cancellationToken)).Throws<LLMProviderException>();
        _ = await Assert.That(fixture.Handler.Requests.Count).IsEqualTo(0);
        _ = await Assert.That(fixture.Credentials.RequestCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Oversized_response_or_non_png_output_is_rejected(bool oversized, CancellationToken cancellationToken)
    {
        using var image = new Image<Rgba32>(1, 1);
        using var stream = new MemoryStream();
        await image.SaveAsJpegAsync(stream, cancellationToken);
        var body = oversized ? SuccessBody : "{\"data\":[{\"b64_json\":\"" + Convert.ToBase64String(stream.ToArray()) + "\"}]}";
        using var fixture = new ProviderFixture("compatible", "https://example.test/v1", body, HttpStatusCode.OK);
        fixture.Handler.AdvertisedLength = oversized ? (64L << 20) + 1 : null;
        _ = await Assert.That(async () => await fixture.Provider.GenerateImage(new("prompt", []), cancellationToken))
            .Throws<LLMProviderException>();
        _ = await Assert.That(fixture.Handler.Requests.Count).IsEqualTo(1);
    }

    [Test]
    [Arguments("compatible")]
    [Arguments("chatgpt")]
    public async Task Missing_credentials_fail_without_http(string providerKind, CancellationToken cancellationToken)
    {
        using var fixture = new ProviderFixture(providerKind, "https://example.test/v1", SuccessBody, HttpStatusCode.OK);
        fixture.Credentials.Missing = true;
        _ = await Assert.That(async () => await fixture.Provider.GenerateImage(new("prompt", []), cancellationToken))
            .Throws<LLMProviderException>();
        _ = await Assert.That(fixture.Handler.Requests.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Caller_cancellation_propagates_before_or_during_send(bool duringSend)
    {
        using var cancellation = new CancellationTokenSource();
        using var fixture = new ProviderFixture("retry", "https://example.test/v1", SuccessBody, HttpStatusCode.OK);
        if (duringSend)
        {
            fixture.Handler.CancelDuringSend = cancellation;
        }
        else
        {
            await cancellation.CancelAsync();
        }

        _ = await Assert.That(async () => await fixture.Provider.GenerateImage(new("prompt", []), cancellation.Token))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(fixture.Handler.Requests.Count).IsEqualTo(duringSend ? 1 : 0);
    }

    [Test]
    public async Task Five_webp_references_are_accepted(CancellationToken cancellationToken)
    {
        using var image = new Image<Rgba32>(1, 1);
        using var stream = new MemoryStream();
        await image.SaveAsWebpAsync(stream, cancellationToken);
        var references = Enumerable.Repeat(new ImageGenerationReference("image/webp", stream.ToArray()), 5).ToArray();
        using var fixture = new ProviderFixture("compatible", "https://example.test/v1", SuccessBody, HttpStatusCode.OK);
        var result = await fixture.Provider.GenerateImage(new("prompt", references), cancellationToken);
        _ = await Assert.That(Convert.ToBase64String(result.Data)).IsEqualTo(PngBase64);
        using var body = JsonDocument.Parse(fixture.Handler.Requests.Single().Body);
        _ = await Assert.That(body.RootElement.GetProperty("images").GetArrayLength()).IsEqualTo(5);
    }

    [Test]
    public async Task Image_operation_does_not_use_short_chat_header_timeout(CancellationToken cancellationToken)
    {
        using var fixture = new ProviderFixture("compatible", "https://example.test/v1", SuccessBody, HttpStatusCode.OK);
        fixture.Handler.HeaderDelay = TimeSpan.FromMilliseconds(50);
        var result = await fixture.Provider.GenerateImage(new("prompt", []), cancellationToken);
        _ = await Assert.That(Convert.ToBase64String(result.Data)).IsEqualTo(PngBase64);
        _ = await Assert.That(fixture.Handler.Requests.Count).IsEqualTo(1);
    }

    private sealed class ProviderFixture : IDisposable
    {
        private readonly HttpClient _client;

        public ProviderFixture(string providerKind, string baseUrl, string body, HttpStatusCode statusCode)
        {
            Handler = new RecordingHandler(body, statusCode);
            _client = new HttpClient(Handler, disposeHandler: false);
            var options = new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = baseUrl,
                ApiKeySource = Credentials,
                HeaderTimeout = TimeSpan.FromMilliseconds(1),
                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Tenant"] = "tenant" },
            };
            Provider = providerKind switch
            {
                "compatible" => new OpenAICompatibleProvider(options, _client),
                "retry" => new RetryingProvider(new OpenAICompatibleProvider(options, _client)),
                "kimi" => new RetryingProvider(new KimiProvider(options, _client)),
                "opencode" => new RetryingProvider(new OpenCodeGoProvider(options, _client)),
                "chatgpt" => new ChatGptProvider(new ImageOAuthTokens(Credentials), _client, [], [], [], true, new ResponsesWebSocketConnector()),
                _ => throw new ArgumentException("Unknown provider", nameof(providerKind)),
            };
        }

        public RecordingHandler Handler { get; }

        public ImageCredentials Credentials { get; } = new();

        public ILLMProvider Provider { get; }

        public void Dispose()
        {
            _client.Dispose();
            Handler.Dispose();
        }
    }

    private sealed class ImageCredentials : IApiKeySource
    {
        public int RequestCount { get; private set; }

        public bool Missing { get; set; }

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(!Missing);

        public ValueTask<string> ApiKey(CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.FromResult(Missing ? string.Empty : $"key-{RequestCount}");
        }
    }

    private sealed class ImageOAuthTokens(IApiKeySource keys) : IOAuthTokenSource
    {
        private int _requestCount;

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => keys.HasCredential(cancellationToken);

        public async Task<OAuthAccess> Token(CancellationToken cancellationToken)
        {
            _requestCount++;
            return new(await keys.ApiKey(cancellationToken), $"account-{_requestCount}");
        }
    }

    private sealed class ImageCredentialStore : ICredentialStore
    {
        private Credential? _credential;

        public ValueTask<Credential?> Get(string name, CancellationToken cancellationToken) => ValueTask.FromResult(_credential);

        public ValueTask Set(string name, Credential credential, CancellationToken cancellationToken)
        {
            _credential = credential;
            return ValueTask.CompletedTask;
        }

        public ValueTask Delete(string name, CancellationToken cancellationToken) => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<string>> List(CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed record CapturedRequest(string Url, HttpMethod Method, string ContentType, string Body, IReadOnlyDictionary<string, string> Headers);

    private sealed class RecordingHandler(string body, HttpStatusCode statusCode) : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public TimeSpan HeaderDelay { get; set; }

        public long? AdvertisedLength { get; set; }

        public CancellationTokenSource? CancelDuringSend { get; set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var content = request.Content ?? throw new InvalidOperationException("Image request has no body.");
            Requests.Add(new(
                request.RequestUri?.AbsoluteUri ?? string.Empty,
                request.Method,
                content.Headers.ContentType?.MediaType ?? string.Empty,
                await content.ReadAsStringAsync(cancellationToken),
                request.Headers.ToDictionary(pair => pair.Key, pair => string.Join(",", pair.Value), StringComparer.OrdinalIgnoreCase)));
            if (CancelDuringSend is not null)
            {
                await CancelDuringSend.CancelAsync();
                cancellationToken.ThrowIfCancellationRequested();
            }

            await Task.Delay(HeaderDelay, cancellationToken);
            var response = new HttpResponseMessage(statusCode) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if (AdvertisedLength is not null)
            {
                response.Content.Headers.ContentLength = AdvertisedLength;
            }

            return response;
        }
    }
}

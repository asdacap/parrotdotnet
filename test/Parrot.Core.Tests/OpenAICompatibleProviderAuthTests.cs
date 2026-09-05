using System.Net;
using System.Text;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class OpenAICompatibleProviderAuthTests
{
    [Test]
    [Arguments("https://example.test/v1", false, false, true)]
    [Arguments("http://example.test/v1", false, false, false)]
    [Arguments("http://example.test/v1", true, false, true)]
    [Arguments("http://127.0.0.1:8000/v1", false, false, false)]
    [Arguments("http://127.0.0.1:8000/v1", false, true, true)]
    public async Task Provider_endpoint_requires_an_explicit_plaintext_exception(
        string baseUrl,
        bool allowInsecureRemote,
        bool allowInsecureLocalhost,
        bool accepted)
    {
        using var client = new HttpClient();
        OpenAICompatibleProvider Build() => new(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = baseUrl,
                ApiKeySource = new RecordingApiKeySource(["key"]),
                AllowInsecureRemote = allowInsecureRemote,
                AllowInsecureLocalhost = allowInsecureLocalhost,
            },
            client);

        if (accepted)
        {
            var provider = Build();
            _ = await Assert.That(provider.Id).IsEqualTo("configured");
        }
        else
        {
            _ = await Assert.That(Build).Throws<ProviderHttpException>();
        }
    }

    [Test]
    public async Task Non_success_responses_preserve_their_body(CancellationToken cancellationToken)
    {
        const string body = """{"error":{"type":"invalid_request","code":"bad","message":"broken"},"trace":"abc"}""";
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.BadRequest)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                ApiKeySource = new RecordingApiKeySource(["key"]),
            },
            client);
        var request = new LLMRequest { Model = "model-a", Messages = [LLMMessage.User("hello")] };
        ProviderHttpException? failure = null;

        try
        {
            _ = await Drain(provider.Call(request, cancellationToken));
        }
        catch (ProviderHttpException caught)
        {
            failure = caught;
        }

        _ = await Assert.That(failure).IsNotNull();
        if (failure is null)
        {
            throw new InvalidOperationException("The provider failure was not raised.");
        }

        _ = await Assert.That(failure.ResponseBody).IsEqualTo(body);
        _ = await Assert.That(failure.Detail).IsEqualTo("broken");
    }

    [Test]
    public async Task Api_key_is_resolved_for_each_request_and_missing_keys_fail_before_http(CancellationToken cancellationToken)
    {
        var source = new RecordingApiKeySource(["first-key", "second-key", string.Empty, string.Empty]);
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"data":[{"id":"model-a","max_input_tokens":1,"max_output_tokens":1,"input_cost_per_token":0,"cache_read_input_token_cost":0,"output_cost_per_token":0,"supports_function_calling":true,"supports_reasoning":false,"supported_output_modalities":["text"],"supported_reasoning_efforts":[]}]}""", Encoding.UTF8, "application/json"),
            },
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"choices":[{"index":0,"finish_reason":"stop","delta":{}}],"usage":{"prompt_tokens":1,"completion_tokens":1}}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            });
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                ApiKeySource = source,
            },
            client);

        var listed = await provider.ListModels(cancellationToken);
        var events = new List<LLMEvent>();
        var request = new LLMRequest { Model = "model-a", Messages = [LLMMessage.User("hello")] };

        await foreach (var item in provider.Call(request, cancellationToken))
        {
            events.Add(item);
        }

        _ = await Assert.That(listed).HasSingleItem();
        _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);
        _ = await Assert.That(handler.AuthorizationHeaders[0]).IsEqualTo("Bearer first-key");
        _ = await Assert.That(handler.AuthorizationHeaders[1]).IsEqualTo("Bearer second-key");
        _ = await Assert.That(async () => await provider.ListModels(cancellationToken)).Throws<LLMProviderException>();
        _ = await Assert.That(async () => await Drain(provider.Call(request, cancellationToken))).Throws<LLMProviderException>();
        _ = await Assert.That(handler.RequestCount).IsEqualTo(2);
    }

    [Test]
    [Arguments("https://example.test/v1", "/v1/models|/v1/model/info")]
    [Arguments("http://example.test:4000", "/models|/model/info")]
    public async Task Missing_metadata_fetches_sibling_model_info_with_one_header_snapshot(
        string baseUrl,
        string expectedPaths,
        CancellationToken cancellationToken)
    {
        using var handler = new RecordingHandler(
            JsonResponse("""{"data":[{"id":"served","max_output_tokens":0,"supports_function_calling":false},{"id":"primary-only"}]}"""),
            JsonResponse("""{"data":[{"model_name":"served","model_info":{"max_input_tokens":512,"max_output_tokens":64,"supports_function_calling":true,"supports_reasoning":true,"reasoning_effort_levels":["low","high"],"default_reasoning_effort":"high"}},{"model_name":"info-only","model_info":{"max_input_tokens":999}}]}"""));
        using var client = new HttpClient(handler, disposeHandler: false);
        var source = new RecordingApiKeySource(["one-key"]);
        var provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = baseUrl,
                ApiKeySource = source,
                AllowInsecureRemote = baseUrl.StartsWith("http:", StringComparison.Ordinal),
                Headers = new Dictionary<string, string>(StringComparer.Ordinal) { ["X-Tenant"] = "tenant" },
            },
            client);

        var listed = await provider.ListModels(cancellationToken);
        var served = listed.Single(model => model.Id == "served");

        _ = await Assert.That(string.Join("|", handler.Paths)).IsEqualTo(expectedPaths);
        _ = await Assert.That(string.Join("|", handler.AuthorizationHeaders))
            .IsEqualTo("Bearer one-key|Bearer one-key");
        _ = await Assert.That(string.Join("|", handler.TenantHeaders)).IsEqualTo("tenant|tenant");
        _ = await Assert.That(source.RequestCount).IsEqualTo(1);
        _ = await Assert.That(string.Join(",", listed.Select(model => model.Id))).IsEqualTo("primary-only,served");
        _ = await Assert.That(served.ContextWindow).IsEqualTo(512);
        _ = await Assert.That(served.MaxOutputTokens).IsEqualTo(0);
        _ = await Assert.That(served.Capabilities.Tools).IsFalse();
        _ = await Assert.That(served.Capabilities.Reasoning).IsTrue();
        _ = await Assert.That(string.Join(",", served.Capabilities.Variants.Select(variant => variant.Name)))
            .IsEqualTo("high,low");
    }

    [Test]
    public async Task Complete_models_metadata_does_not_probe_model_info(CancellationToken cancellationToken)
    {
        const string completeModels = """
            {"data":[{"id":"complete","max_input_tokens":0,"max_output_tokens":0,"input_cost_per_token":0,"cache_read_input_token_cost":0,"output_cost_per_token":0,"supports_function_calling":false,"supports_reasoning":false,"supported_output_modalities":[],"supported_reasoning_efforts":[]}]}
            """;
        using var handler = new RecordingHandler(JsonResponse(completeModels));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = Provider(client, new RecordingApiKeySource(["key"]));

        var listed = await provider.ListModels(cancellationToken);

        _ = await Assert.That(listed).HasSingleItem();
        _ = await Assert.That(string.Join("|", handler.Paths)).IsEqualTo("/v1/models");
    }

    [Test]
    [Arguments(HttpStatusCode.NotFound, "missing")]
    [Arguments(HttpStatusCode.Unauthorized, "unauthorized")]
    [Arguments(HttpStatusCode.InternalServerError, "broken")]
    [Arguments(HttpStatusCode.OK, "not json")]
    [Arguments(HttpStatusCode.OK, "{}")]
    public async Task Optional_model_info_failure_keeps_models_refresh(
        HttpStatusCode statusCode,
        string modelInfoBody,
        CancellationToken cancellationToken)
    {
        using var handler = new RecordingHandler(
            JsonResponse("""{"data":[{"id":"served"}]}"""),
            new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(modelInfoBody, Encoding.UTF8, "application/json"),
            });
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = Provider(client, new RecordingApiKeySource(["key"]));

        var listed = await provider.ListModels(cancellationToken);

        _ = await Assert.That(listed.Single().Id).IsEqualTo("served");
        _ = await Assert.That(string.Join("|", handler.Paths)).IsEqualTo("/v1/models|/v1/model/info");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Optional_model_info_transport_or_timeout_failure_keeps_models_refresh(
        bool timeout,
        CancellationToken cancellationToken)
    {
        Exception failure = timeout
            ? new OperationCanceledException("timed out")
            : new HttpRequestException("unavailable");
        using var handler = new ModelInfoFailureHandler(failure, null);
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = Provider(client, new RecordingApiKeySource(["key"]));

        var listed = await provider.ListModels(cancellationToken);

        _ = await Assert.That(listed.Single().Id).IsEqualTo("served");
        _ = await Assert.That(handler.RequestCount).IsEqualTo(2);
    }

    [Test]
    public async Task Caller_cancellation_during_model_info_propagates()
    {
        using var cancellation = new CancellationTokenSource();
        using var handler = new ModelInfoFailureHandler(new OperationCanceledException(cancellation.Token), cancellation);
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = Provider(client, new RecordingApiKeySource(["key"]));

        _ = await Assert.That(async () => await provider.ListModels(cancellation.Token))
            .Throws<OperationCanceledException>();
        _ = await Assert.That(handler.RequestCount).IsEqualTo(2);
    }

    private static OpenAICompatibleProvider Provider(HttpClient client, IApiKeySource apiKeySource) => new(
        new OpenAICompatibleOptions
        {
            Id = "configured",
            BaseUrl = "https://example.test/v1",
            ApiKeySource = apiKeySource,
        },
        client);

    private static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private static async Task<List<LLMEvent>> Drain(IAsyncEnumerable<LLMEvent> events)
    {
        var result = new List<LLMEvent>();

        await foreach (var item in events)
        {
            result.Add(item);
        }

        return result;
    }

    private sealed class RecordingApiKeySource(IEnumerable<string> keys) : IApiKeySource
    {
        private readonly Queue<string> _keys = new(keys);

        public int RequestCount { get; private set; }

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<string> ApiKey(CancellationToken cancellationToken)
        {
            RequestCount++;
            return ValueTask.FromResult(_keys.Dequeue());
        }
    }

    private sealed class ModelInfoFailureHandler(
        Exception failure,
        CancellationTokenSource? cancellation) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;

            if (RequestCount == 1)
            {
                return JsonResponse("""{"data":[{"id":"served"}]}""");
            }

            if (cancellation is not null)
            {
                await cancellation.CancelAsync();
            }

            return await Task.FromException<HttpResponseMessage>(failure);
        }
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> AuthorizationHeaders { get; } = [];

        public List<string> TenantHeaders { get; } = [];

        public List<string> Paths { get; } = [];

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            TenantHeaders.Add(request.Headers.TryGetValues("X-Tenant", out var values)
                ? values.Single()
                : string.Empty);
            Paths.Add(request.RequestUri?.AbsolutePath ?? string.Empty);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}

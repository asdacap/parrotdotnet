using System.Net;
using System.Text;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class OpenAICompatibleProviderAuthTests
{
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
                    """{"data":[{"id":"model-a"}]}""", Encoding.UTF8, "application/json"),
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

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<string> ApiKey(CancellationToken cancellationToken) =>
            ValueTask.FromResult(_keys.Dequeue());
    }

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> _responses = new(responses);

        public List<string> AuthorizationHeaders { get; } = [];

        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString() ?? string.Empty);
            return Task.FromResult(_responses.Dequeue());
        }
    }
}

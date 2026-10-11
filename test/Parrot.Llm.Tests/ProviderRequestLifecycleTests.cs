using System.Net;
using System.Text;
using System.Text.Json;
using Parrot.Auth;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ProviderRequestLifecycleTests
{
    [Test]
    [Arguments("chatgpt", false)]
    [Arguments("chatgpt", true)]
    [Arguments("responses", false)]
    [Arguments("responses", true)]
    [Arguments("chat", false)]
    [Arguments("chat", true)]
    public async Task Http_phases_precede_headers_and_body_and_dispose_response(
        string dialect, bool useSession, CancellationToken cancellationToken)
    {
        using var body = new ObservedStream();
        using var handler = new HeaderBarrierHandler(body);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = dialect == "chatgpt"
            ? new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], true, new ResponsesWebSocketConnector())
            : new OpenAICompatibleProvider(
                new OpenAICompatibleOptions
                {
                    Id = "test",
                    BaseUrl = "https://provider.invalid/v1",
                    ApiKeySource = new FixedApiKeySource(),
                    Protocol = dialect == "responses" ? CompatibleProtocol.Responses : CompatibleProtocol.ChatCompletions,
                },
                client);
        await using var session = provider.OpenSession();
        var request = new LLMRequest { Model = "model", Messages = [] };
        var events = useSession ? session.Call(request, cancellationToken) : provider.Call(request, cancellationToken);
        await using (var enumerator = events.GetAsyncEnumerator(cancellationToken))
        {
            _ = await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
            _ = await Assert.That(enumerator.Current.Kind).IsEqualTo(LLMEventKind.HttpRequestStarted);
            var next = enumerator.MoveNextAsync().AsTask();
            await handler.RequestEntered.Task.WaitAsync(cancellationToken);
            _ = await Assert.That(next.IsCompleted).IsFalse();
            handler.ReleaseHeaders.SetResult();
            _ = await Assert.That(await next).IsTrue();
            _ = await Assert.That(enumerator.Current.Kind).IsEqualTo(LLMEventKind.HttpResponseHeadersReceived);
            _ = await Assert.That(body.ReadCount).IsEqualTo(0);
            _ = await Assert.That(body.Disposed).IsFalse();
        }

        _ = await Assert.That(body.Disposed).IsTrue();
    }

    [Test]
    public async Task Compatible_http_replays_reasoning_by_default_across_model_changes(
        CancellationToken cancellationToken)
    {
        using var handler = new ReasoningReplayHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured-provider",
                BaseUrl = "https://provider.invalid/v1",
                ApiKeySource = new FixedApiKeySource(),
            },
            client);
        LLMEvent? completed = null;
        await foreach (var published in provider.Call(
            new LLMRequest
            {
                Model = "first-model",
                Messages = [LLMMessage.User("hello")],
            },
            cancellationToken))
        {
            if (published.Kind == LLMEventKind.Completed)
            {
                completed = published;
            }
        }

        var outcome = completed ?? throw new InvalidOperationException("No completed response.");
        _ = await Assert.That(outcome.ProviderContent.ProviderId).IsEqualTo("configured-provider");
        await foreach (var published in provider.Call(
            new LLMRequest
            {
                Model = "second-model",
                Messages = [LLMMessage.User("hello"), LLMMessage.Assistant(outcome.AssistantText, outcome.ToolCalls) with
                {
                    ProviderContent = outcome.ProviderContent,
                }, LLMMessage.User("continue")],
            },
            cancellationToken))
        {
            _ = published;
        }

        using var document = JsonDocument.Parse(handler.LastRequest);
        _ = await Assert.That(handler.RequestCount).IsEqualTo(2);
        _ = await Assert.That(document.RootElement.GetProperty("model").GetString()).IsEqualTo("second-model");
        var assistant = document.RootElement.GetProperty("messages")[1];
        _ = await Assert.That(assistant.GetProperty("reasoning_content").GetString()).IsEqualTo("native thought");
        _ = await Assert.That(assistant.GetProperty("reasoning").GetString()).IsEqualTo("alternate thought");
        _ = await Assert.That(assistant.GetProperty("content").GetString()).IsEqualTo("answer");
    }

    [Test]
    [Arguments("responses", false)]
    [Arguments("chatgpt", false)]
    [Arguments("responses", true)]
    [Arguments("chatgpt", true)]
    [Arguments("chat", true)]
    public async Task Native_http_reasoning_is_replayed_and_rejection_does_not_strip_it(
        string dialect, bool rejectContinuation, CancellationToken cancellationToken)
    {
        using var handler = new NativeHistoryHandler(dialect, rejectContinuation);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = dialect == "chatgpt"
            ? new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], true, new ResponsesWebSocketConnector())
            : new OpenAICompatibleProvider(
                new OpenAICompatibleOptions
                {
                    Id = "configured-provider",
                    BaseUrl = "https://provider.invalid/v1",
                    ApiKeySource = new FixedApiKeySource(),
                    Protocol = dialect == "chat" ? CompatibleProtocol.ChatCompletions : CompatibleProtocol.Responses,
                },
                client);
        await using var session = provider.OpenSession();
        var initial = new LLMRequest { Model = "first-model", Messages = [LLMMessage.User("hello")] };
        LLMEvent? completed = null;
        await foreach (var published in session.Call(initial, cancellationToken))
        {
            if (published.Kind == LLMEventKind.Completed)
            {
                completed = published;
            }
        }

        var outcome = completed ?? throw new InvalidOperationException("No completed response.");
        _ = await Assert.That(outcome.ProviderContent.ProviderId).IsEqualTo(provider.Id);
        _ = await Assert.That(outcome.AssistantText).IsEqualTo("answer");
        var continued = new LLMRequest
        {
            Model = "second-model",
            Messages = [.. initial.Messages, LLMMessage.Assistant(outcome.AssistantText, outcome.ToolCalls) with
            {
                ProviderContent = outcome.ProviderContent,
            }, LLMMessage.User("next")],
        };
        var retries = 0;
        async Task Continue()
        {
            await foreach (var published in session.Call(continued, cancellationToken))
            {
                if (published.Kind == LLMEventKind.Retry)
                {
                    retries++;
                }
            }
        }

        if (rejectContinuation)
        {
            _ = await Assert.That(Continue).Throws<ProviderHttpException>();
        }
        else
        {
            await Continue();
        }

        _ = await Assert.That(handler.Requests.Count).IsEqualTo(2);
        _ = await Assert.That(retries).IsEqualTo(0);
        using var encoded = JsonDocument.Parse(handler.Requests[1]);
        if (dialect == "chat")
        {
            var assistant = encoded.RootElement.GetProperty("messages")[1];
            _ = await Assert.That(assistant.GetProperty("reasoning_content").GetString()).IsEqualTo("native thought");
            _ = await Assert.That(assistant.GetProperty("content").GetString()).IsEqualTo("answer");
        }
        else
        {
            _ = await Assert.That(encoded.RootElement.GetProperty("include")[0].GetString()).IsEqualTo("reasoning.encrypted_content");
            var input = encoded.RootElement.GetProperty("input");
            _ = await Assert.That(input.GetArrayLength()).IsEqualTo(4);
            _ = await Assert.That(input[1].GetProperty("encrypted_content").GetString()).IsEqualTo("opaque-reasoning");
            _ = await Assert.That(input[1].GetProperty("id").GetString()).IsEqualTo("rs_1");
            _ = await Assert.That(input[2].GetProperty("content")[0].GetProperty("text").GetString()).IsEqualTo("answer");
        }
    }

    private sealed class NativeHistoryHandler(string dialect, bool rejectContinuation) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await (request.Content ?? throw new InvalidOperationException("Missing request body."))
                .ReadAsStringAsync(cancellationToken));
            if (rejectContinuation && Requests.Count > 1)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"error\":{\"code\":\"invalid_reasoning\",\"message\":\"reasoning rejected\"}}"),
                };
            }

            const string responses = """
                data: {"type":"response.output_text.delta","delta":"answer"}

                data: {"type":"response.output_item.done","output_index":0,"item":{"id":"rs_1","type":"reasoning","encrypted_content":"opaque-reasoning","summary":[{"type":"summary_text","text":"summary"}]}}

                data: {"type":"response.output_item.done","output_index":1,"item":{"id":"msg_1","type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}}

                data: {"type":"response.completed","response":{"id":"resp_1","output":[]}}

                """;
            const string chat = """
                data: {"choices":[{"delta":{"reasoning_content":"native thought","content":"answer"},"finish_reason":"stop"}]}

                data: [DONE]

                """;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(dialect == "chat" ? chat : responses, Encoding.UTF8, "text/event-stream"),
            };
        }
    }

    private sealed class ReasoningReplayHandler : HttpMessageHandler
    {
        public string LastRequest { get; private set; } = string.Empty;

        public int RequestCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = await (request.Content ?? throw new InvalidOperationException("Missing request body."))
                .ReadAsStringAsync(cancellationToken);
            RequestCount++;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """
                    data: {"choices":[{"delta":{"reasoning_content":"native thought","reasoning":"alternate thought","content":"answer"},"finish_reason":"stop"}]}

                    data: [DONE]

                    """,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }

    private sealed class HeaderBarrierHandler(Stream body) : HttpMessageHandler
    {
        public TaskCompletionSource RequestEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseHeaders { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestEntered.SetResult();
            await ReleaseHeaders.Task.WaitAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) };
        }
    }

    private sealed class ObservedStream : MemoryStream
    {
        public int ReadCount { get; private set; }

        public bool Disposed { get; private set; }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            ReadCount++;
            return base.ReadAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class FixedOAuthTokenSource : IOAuthTokenSource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<OAuthAccess> Token(CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthAccess("test-token", "test-account"));
    }

    private sealed class FixedApiKeySource : IApiKeySource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public ValueTask<string> ApiKey(CancellationToken cancellationToken) => ValueTask.FromResult("test-key");
    }
}

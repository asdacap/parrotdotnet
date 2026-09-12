using System.Net;
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

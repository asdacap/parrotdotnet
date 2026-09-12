using System.Net;
using System.Net.WebSockets;
using Parrot.Auth;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ProviderStreamIdleTimeoutTests
{
    [Test]
    [Arguments("chatgpt", false, 20)]
    [Arguments("chatgpt", true, 20)]
    [Arguments("responses", false, 20)]
    [Arguments("responses", true, 20)]
    [Arguments("chat-completions", false, 20)]
    [Arguments("chatgpt", false, 0)]
    [Arguments("chatgpt", true, 0)]
    [Arguments("responses", false, 0)]
    [Arguments("responses", true, 0)]
    [Arguments("chat-completions", false, 0)]
    [Timeout(10_000)]
    public async Task Http_body_idle_timeout_applies_to_each_protocol_and_websocket_fallback(
        string transport,
        bool fallback,
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var body = new StalledStream();
        using var handler = new StalledBodyHandler(body);
        using var client = new HttpClient(handler, disposeHandler: false);
        var connector = new UnsupportedConnector();
        using var log = new RecordingLog();
        ILLMProvider provider = transport == "chatgpt"
            ? new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], !fallback, connector)
            {
                StreamIdleTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
            }
            : new OpenAICompatibleProvider(
                new OpenAICompatibleOptions
                {
                    Id = "configured",
                    BaseUrl = "https://example.test/v1",
                    Protocol = transport == "responses" ? CompatibleProtocol.Responses : CompatibleProtocol.ChatCompletions,
                    ApiKeySource = new FixedApiKeySource(),
                    DisableWebSocket = !fallback,
                    StreamIdleTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
                },
                client,
                connector);
        await using var session = provider.OpenSession();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        async Task Consume()
        {
            await foreach (var published in session.Call(
                new LLMRequest
                {
                    Model = "model",
                    Messages = [LLMMessage.User("hello")],
                    Diagnostics = new ProviderRequestDiagnostics(log, new DiagnosticEvent("provider", "call", DiagnosticSeverity.Information), null),
                },
                caller.Token))
            {
                _ = await Assert.That(published.Kind is LLMEventKind.Retry
                    or LLMEventKind.HttpRequestStarted or LLMEventKind.HttpResponseHeadersReceived).IsTrue();
            }
        }

        var consuming = Consume();
        _ = await Task.WhenAny(body.ReadStarted.Task, consuming).WaitAsync(cancellationToken);
        if (consuming.IsCompleted)
        {
            await consuming.WaitAsync(cancellationToken);
        }

        if (timeoutMilliseconds == 0)
        {
            await Task.Delay(100, cancellationToken);
            _ = await Assert.That(consuming.IsCompleted).IsFalse();
            await caller.CancelAsync();
            _ = await Assert.That(async () => await consuming.WaitAsync(cancellationToken)).Throws<OperationCanceledException>();
        }
        else
        {
            _ = await Assert.That(async () => await consuming.WaitAsync(cancellationToken)).Throws<WireProtocolException>()
                .WithMessageContaining("idle");
        }

        var finished = log.Entries.Single(entry => entry.Operation == "request_finished" && entry.Transport == "http_sse");
        _ = await Assert.That(finished.Outcome).IsEqualTo(timeoutMilliseconds == 0 ? "cancelled" : "failed");
        _ = await Assert.That(finished.ResponseBytes).IsEqualTo(0L);
        _ = await Assert.That(finished.DurationMilliseconds >= 0).IsTrue();
        _ = await Assert.That(body.Disposed).IsTrue();
        _ = await Assert.That(handler.Calls).IsEqualTo(1);
        _ = await Assert.That(connector.Calls).IsEqualTo(fallback ? 1 : 0);
    }

    private sealed class RecordingLog : IDiagnosticLog
    {
        public List<DiagnosticEvent> Entries { get; } = [];

        public void Write(DiagnosticEvent entry) => Entries.Add(entry);

        public void Dispose()
        {
        }
    }

    private sealed class FixedOAuthTokenSource : IOAuthTokenSource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<OAuthAccess> Token(CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthAccess("access-token", "account-id"));
    }

    private sealed class FixedApiKeySource : IApiKeySource
    {
        public ValueTask<string> ApiKey(CancellationToken cancellationToken) => ValueTask.FromResult("key");

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class UnsupportedConnector : IResponsesWebSocketConnector
    {
        public int Calls { get; private set; }

        public Task<(WebSocket Socket, IReadOnlyDictionary<string, string> ResponseHeaders)> Connect(
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new ResponsesWebSocketUpgradeException(404, "missing", new IOException());
        }
    }

    private sealed class StalledBodyHandler(Stream body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(body),
            });
        }
    }

    private sealed class StalledStream : Stream
    {
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
        {
            _ = ReadStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite delay completed without cancellation.");
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override void Flush() => throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            Disposed = true;
            base.Dispose(disposing);
        }
    }
}

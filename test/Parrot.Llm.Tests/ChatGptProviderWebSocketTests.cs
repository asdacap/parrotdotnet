using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Parrot.Auth;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ChatGptProviderWebSocketTests
{
    private const string Completed =
        "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"usage\":{\"input_tokens\":1,\"output_tokens\":1},\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"answer\"}]}]}}";

    [Test]
    [Arguments(0)]
    [Arguments(1250)]
    public async Task Configured_header_timeout_reaches_websocket_connector(
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([Completed]);
        var connector = new RecordingConnector([socket]);
        using var handler = new UnexpectedHttpHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], false, connector)
        {
            HeaderTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
        };
        await using var session = provider.OpenSession();

        var events = await Drain(session.Call(
            new LLMRequest { Model = "model", Messages = [LLMMessage.User("hello")] }, cancellationToken));

        _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);
        _ = await Assert.That(connector.ConnectTimeout).IsEqualTo(TimeSpan.FromMilliseconds(timeoutMilliseconds));
    }

    [Test]
    [Arguments(0, false)]
    [Arguments(0, true)]
    [Arguments(20, false)]
    [Arguments(20, true)]
    [Timeout(10_000)]
    public async Task Configured_header_timeout_applies_to_http_and_websocket_fallback(
        int timeoutMilliseconds,
        bool fallback,
        CancellationToken cancellationToken)
    {
        var connector = new RecordingConnector(fallback
            ? [new ResponsesWebSocketUpgradeException(404, "missing", new IOException())]
            : []);
        using var handler = new ResponsesHandler()
        {
            HeaderDelay = TimeSpan.FromMilliseconds(250),
        };
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], !fallback, connector)
        {
            HeaderTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
        };
        await using var session = provider.OpenSession();

        async Task Consume() => _ = await Drain(session.Call(
            new LLMRequest { Model = "model", Messages = [LLMMessage.User("hello")] }, cancellationToken));

        if (timeoutMilliseconds == 0)
        {
            await Consume();
            _ = await Assert.That(handler.Calls).IsEqualTo(1);
        }
        else
        {
            _ = await Assert.That(Consume).Throws<HeaderTimeoutException>();
        }

        _ = await Assert.That(connector.Calls).IsEqualTo(fallback ? 1 : 0);
    }

    [Test]
    [Arguments("reuse")]
    [Arguments("fallback")]
    [Arguments("previous_response_not_found")]
    [Arguments("websocket_connection_limit_reached")]
    public async Task Physical_websocket_attempts_include_recovery_fallback_and_reused_connections(
        string behavior,
        CancellationToken cancellationToken)
    {
        using var log = new ProviderRequestLog();
        var completed = Completed.Replace("answer", "private-sentinel-response", StringComparison.Ordinal);
        using var recovered = new ScriptedWebSocket([completed, completed]);
        using var failed = new ScriptedWebSocket([$"{{\"type\":\"error\",\"error\":{{\"code\":\"{behavior}\",\"message\":\"private-sentinel-error\"}}}}"]);
        var connector = new RecordingConnector(behavior == "reuse"
            ? [recovered]
            : behavior == "fallback"
                ? [new ResponsesWebSocketUpgradeException(404, "private-sentinel-upgrade", new IOException("private-sentinel-exception"))]
                : [failed, recovered]);
        using var handler = new ResponsesHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], false, connector);
        var sessions = log.OpenSessions("websocket-agent");
        try
        {
            var session = sessions.Get(provider);
            var events = await Drain(session.Call(
                new LLMRequest { Model = "model", Messages = [LLMMessage.User("private-sentinel-prompt")] }, cancellationToken));
            _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);
            if (behavior == "reuse")
            {
                _ = await Drain(session.Call(
                    new LLMRequest
                    {
                        Model = "model",
                        Messages = [LLMMessage.User("private-sentinel-next")],
                    },
                    cancellationToken));
            }
        }
        finally
        {
            await sessions.Close();
        }

        _ = await Assert.That(connector.Calls).IsEqualTo(behavior is "reuse" or "fallback" ? 1 : 2);
        _ = await Assert.That(handler.Calls).IsEqualTo(behavior == "fallback" ? 1 : 0);
        _ = await Assert.That(recovered.Sent.Count + failed.Sent.Count).IsEqualTo(behavior == "fallback" ? 0 : 2);
        await log.AssertAttempts(
            "websocket-agent",
            behavior == "fallback" ? ["websocket", "http_sse"] : ["websocket", "websocket"],
            behavior == "reuse" ? ["completed", "completed"] : ["failed", "completed"],
            behavior == "reuse" ? 2 : 1);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    public async Task Configured_request_limit_applies_to_http_websocket_and_fallback(
        bool disableWebSocket,
        bool fallback,
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([]);
        var connector = new RecordingConnector(fallback
            ? [new ResponsesWebSocketUpgradeException(404, "missing", new IOException())]
            : [socket]);
        using var handler = new ResponsesHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], disableWebSocket, connector) { MaximumRequestBytes = 1 };
        await using var session = provider.OpenSession();

        async Task Consume() => _ = await Drain(session.Call(
            new LLMRequest { Model = "model", Messages = [LLMMessage.User("hello")] }, cancellationToken));

        if (!disableWebSocket && !fallback)
        {
            var failure = await Assert.That(Consume).Throws<ResponsesWebSocketTransportException>();
            var requestFailure = failure?.InnerException;
            _ = await Assert.That(requestFailure).IsTypeOf<ProviderHttpException>();
            _ = await Assert.That(requestFailure?.Message).Contains("exceeds 1 bytes");
        }
        else
        {
            _ = await Assert.That(Consume).Throws<ProviderHttpException>().WithMessageContaining("exceeds 1 bytes");
        }

        _ = await Assert.That(handler.Calls).IsEqualTo(0);
        _ = await Assert.That(socket.Sent.Count).IsEqualTo(0);
    }

    [Test]
    public async Task Session_uses_chatgpt_websocket_headers_and_reuses_incremental_connection(
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([
            Completed,
            Completed.Replace("resp-1", "resp-2", StringComparison.Ordinal),
        ]);
        var connector = new RecordingConnector([socket]);
        using var handler = new UnexpectedHttpHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], false, connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "gpt-5.6-sol", MaxOutputTokens = 4096, Messages = [LLMMessage.User("hello")] }, cancellationToken));
        var continued = new LLMRequest
        {
            Model = "gpt-5.6-sol",
            MaxOutputTokens = 4096,
            Messages =
            [
                LLMMessage.User("hello"),
                LLMMessage.Assistant("answer", []),
                LLMMessage.User("next"),
            ],
        };
        _ = await Drain(session.Call(continued, cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(connector.ConnectTimeout).IsEqualTo(TimeSpan.FromSeconds(60));
        _ = await Assert.That(connector.Endpoint).IsEqualTo(new Uri("wss://chatgpt.com/backend-api/codex/responses"));
        _ = await Assert.That(connector.Headers["OpenAI-Beta"]).IsEqualTo("responses_websockets=2026-02-06");
        _ = await Assert.That(connector.Headers["Authorization"]).IsEqualTo("Bearer access-token");
        _ = await Assert.That(connector.Headers["originator"]).IsEqualTo("parrot");
        _ = await Assert.That(connector.Headers["User-Agent"]).IsEqualTo("parrot");
        _ = await Assert.That(connector.Headers["ChatGPT-Account-Id"]).IsEqualTo("account-id");
        _ = await Assert.That(connector.Headers["session-id"].Length).IsGreaterThan(0);

        using var first = JsonDocument.Parse(socket.Sent[0]);
        using var second = JsonDocument.Parse(socket.Sent[1]);
        _ = await Assert.That(first.RootElement.TryGetProperty("max_output_tokens", out _)).IsFalse();
        _ = await Assert.That(second.RootElement.GetProperty("previous_response_id").GetString()).IsEqualTo("resp-1");
        _ = await Assert.That(second.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(1);
        _ = await Assert.That(second.RootElement.GetProperty("input")[0].GetProperty("content")[0]
            .GetProperty("text").GetString()).IsEqualTo("next");
    }

    [Test]
    public async Task Unsupported_upgrade_falls_back_to_http_stickily(CancellationToken cancellationToken)
    {
        var connector = new RecordingConnector([
            new ResponsesWebSocketUpgradeException(404, "missing", new IOException()),
        ]);
        using var handler = new ResponsesHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], false, connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "gpt-5.6-sol", MaxOutputTokens = 4096, Messages = [LLMMessage.User("one")] }, cancellationToken));
        _ = await Drain(session.Call(new LLMRequest { Model = "gpt-5.6-sol", MaxOutputTokens = 4096, Messages = [LLMMessage.User("two")] }, cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(handler.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Disabled_websocket_uses_isolated_stable_http_sessions(
        CancellationToken cancellationToken)
    {
        var connector = new RecordingConnector([]);
        using var handler = new ResponsesHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], true, connector);
        await using var first = provider.OpenSession();
        await using var second = provider.OpenSession();

        _ = await Drain(first.Call(new LLMRequest { Model = "gpt-5.6-sol", MaxOutputTokens = 4096, Messages = [LLMMessage.User("one")] }, cancellationToken));
        _ = await Drain(first.Call(new LLMRequest { Model = "gpt-5.6-sol", MaxOutputTokens = 4096, Messages = [LLMMessage.User("two")] }, cancellationToken));
        _ = await Drain(second.Call(new LLMRequest { Model = "gpt-5.6-sol", MaxOutputTokens = 4096, Messages = [LLMMessage.User("three")] }, cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(0);
        _ = await Assert.That(handler.SessionIds).Count().IsEqualTo(3);
        _ = await Assert.That(handler.SessionIds[0].Length).IsGreaterThan(0);
        _ = await Assert.That(handler.SessionIds[1]).IsEqualTo(handler.SessionIds[0]);
        _ = await Assert.That(handler.SessionIds[2]).IsNotEqualTo(handler.SessionIds[0]);
    }

    [Test]
    public async Task Sessions_have_independent_connections_and_session_headers(CancellationToken cancellationToken)
    {
        using var firstSocket = new ScriptedWebSocket([Completed]);
        using var secondSocket = new ScriptedWebSocket([Completed]);
        var connector = new RecordingConnector([firstSocket, secondSocket]);
        using var handler = new UnexpectedHttpHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new ChatGptProvider(new FixedOAuthTokenSource(), client, [], [], [], false, connector);
        await using var first = provider.OpenSession();
        await using var second = provider.OpenSession();

        _ = await Drain(first.Call(new LLMRequest { Model = "gpt-5.6-sol", MaxOutputTokens = 4096, Messages = [LLMMessage.User("one")] }, cancellationToken));
        var firstSessionId = connector.HeadersByCall[0]["session-id"];
        _ = await Drain(second.Call(new LLMRequest { Model = "gpt-5.6-sol", MaxOutputTokens = 4096, Messages = [LLMMessage.User("two")] }, cancellationToken));
        var secondSessionId = connector.HeadersByCall[1]["session-id"];

        _ = await Assert.That(connector.Calls).IsEqualTo(2);
        _ = await Assert.That(firstSessionId).IsNotEqualTo(secondSessionId);
        _ = await Assert.That(firstSocket.Sent).HasSingleItem();
        _ = await Assert.That(secondSocket.Sent).HasSingleItem();
    }

    private static async Task<List<LLMEvent>> Drain(IAsyncEnumerable<LLMEvent> events)
    {
        var result = new List<LLMEvent>();
        await foreach (var published in events)
        {
            result.Add(published);
        }

        return result;
    }

    private sealed class FixedOAuthTokenSource : IOAuthTokenSource
    {
        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);

        public Task<OAuthAccess> Token(CancellationToken cancellationToken) =>
            Task.FromResult(new OAuthAccess("access-token", "account-id"));
    }

    private sealed class RecordingConnector(IEnumerable<object> outcomes) : IResponsesWebSocketConnector
    {
        private readonly Queue<object> _outcomes = new(outcomes);

        public Uri Endpoint { get; private set; } = new("wss://unset.invalid");

        public IReadOnlyDictionary<string, string> Headers { get; private set; } =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        public List<IReadOnlyDictionary<string, string>> HeadersByCall { get; } = [];

        public TimeSpan ConnectTimeout { get; private set; }

        public int Calls { get; private set; }

        public Task<(WebSocket Socket, IReadOnlyDictionary<string, string> ResponseHeaders)> Connect(
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            ConnectTimeout = timeout;
            Endpoint = endpoint;
            Headers = headers;
            HeadersByCall.Add(headers);
            return _outcomes.Dequeue() switch
            {
                WebSocket socket => Task.FromResult((socket, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
                Exception failure => Task.FromException<(WebSocket Socket, IReadOnlyDictionary<string, string> ResponseHeaders)>(failure),
                _ => throw new InvalidOperationException("Unknown WebSocket outcome."),
            };
        }
    }

    private sealed class ScriptedWebSocket(IEnumerable<string> responses) : WebSocket
    {
        private readonly Queue<byte[]> _responses = new(responses.Select(Encoding.UTF8.GetBytes));
        private WebSocketState _state = WebSocketState.Open;

        public List<byte[]> Sent { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override WebSocketState State => _state;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken) => CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_responses.TryDequeue(out var response))
            {
                response.AsSpan().CopyTo(buffer.AsSpan());
                return Task.FromResult(new WebSocketReceiveResult(response.Length, WebSocketMessageType.Text, true));
            }

            return WaitForCancellation(cancellationToken);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Sent.Add([.. buffer]);
            return Task.CompletedTask;
        }

        private static async Task<WebSocketReceiveResult> WaitForCancellation(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite delay completed without cancellation.");
        }
    }

    private sealed class UnexpectedHttpHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP was not expected.");
    }

    private sealed class ResponsesHandler : HttpMessageHandler
    {
        public List<string> SessionIds { get; } = [];

        public int Calls => SessionIds.Count;

        public TimeSpan HeaderDelay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            SessionIds.Add(string.Join(',', request.Headers.GetValues("session-id")));
            await Task.Delay(HeaderDelay, cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }
}

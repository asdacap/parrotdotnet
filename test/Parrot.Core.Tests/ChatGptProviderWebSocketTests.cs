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
        var provider = Provider(client, connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(Request([LLMMessage.User("hello")]), cancellationToken));
        var continued = Request([
            LLMMessage.User("hello"),
            LLMMessage.Assistant("answer", []),
            LLMMessage.User("next"),
        ]);
        _ = await Drain(session.Call(continued, cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(1);
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
        var provider = Provider(client, connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(Request([LLMMessage.User("one")]), cancellationToken));
        _ = await Drain(session.Call(Request([LLMMessage.User("two")]), cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(handler.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Disabled_websocket_uses_http_without_connecting(
        CancellationToken cancellationToken)
    {
        var connector = new RecordingConnector([]);
        using var handler = new ResponsesHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = ProviderWithSetting(client, connector, true);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(Request([LLMMessage.User("one")]), cancellationToken));
        _ = await Drain(session.Call(Request([LLMMessage.User("two")]), cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(0);
        _ = await Assert.That(handler.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Sessions_have_independent_connections_and_session_headers(CancellationToken cancellationToken)
    {
        using var firstSocket = new ScriptedWebSocket([Completed]);
        using var secondSocket = new ScriptedWebSocket([Completed]);
        var connector = new RecordingConnector([firstSocket, secondSocket]);
        using var handler = new UnexpectedHttpHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = Provider(client, connector);
        await using var first = provider.OpenSession();
        await using var second = provider.OpenSession();

        _ = await Drain(first.Call(Request([LLMMessage.User("one")]), cancellationToken));
        var firstSessionId = connector.HeadersByCall[0]["session-id"];
        _ = await Drain(second.Call(Request([LLMMessage.User("two")]), cancellationToken));
        var secondSessionId = connector.HeadersByCall[1]["session-id"];

        _ = await Assert.That(connector.Calls).IsEqualTo(2);
        _ = await Assert.That(firstSessionId).IsNotEqualTo(secondSessionId);
        _ = await Assert.That(firstSocket.Sent).HasSingleItem();
        _ = await Assert.That(secondSocket.Sent).HasSingleItem();
    }

    private static ChatGptProvider Provider(HttpClient client, IResponsesWebSocketConnector connector) =>
        ProviderWithSetting(client, connector, false);

    private static ChatGptProvider ProviderWithSetting(
        HttpClient client,
        IResponsesWebSocketConnector connector,
        bool disableWebSocket) =>
        new(new FixedOAuthTokenSource(), client, [], [], disableWebSocket, connector);

    private static LLMRequest Request(IReadOnlyList<LLMMessage> messages) =>
        new() { Model = "gpt-5.6-sol", MaxTokens = 4096, Messages = messages };

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

        public int Calls { get; private set; }

        public Task<(WebSocket Socket, IReadOnlyDictionary<string, string> ResponseHeaders)> Connect(
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls++;
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
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            });
        }
    }
}

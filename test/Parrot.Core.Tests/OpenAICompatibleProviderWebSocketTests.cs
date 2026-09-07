using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class OpenAICompatibleProviderWebSocketTests
{
    private const string Completed = """
        {"type":"response.completed","response":{"id":"resp-1","usage":{"input_tokens":1,"output_tokens":1},"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}}
        """;

    [Test]
    public async Task Compatible_calls_reuse_the_socket_and_send_only_the_verified_suffix(
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([Completed, Completed.Replace("resp-1", "resp-2", StringComparison.Ordinal)]);
        var connector = new ScriptedConnector([socket]);
        using var handler = new EmptyHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("hello")] }, cancellationToken));
        var continued = new LLMRequest
        {
            Model = "model",
            Messages =
            [
                LLMMessage.User("hello"),
                LLMMessage.Assistant("answer", []),
                LLMMessage.User("next"),
            ],
        };
        _ = await Drain(session.Call(continued, cancellationToken));

        using var second = JsonDocument.Parse(socket.Sent[1]);
        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(socket.Sent).Count().IsEqualTo(2);
        _ = await Assert.That(second.RootElement.GetProperty("previous_response_id").GetString()).IsEqualTo("resp-1");
        _ = await Assert.That(second.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(1);
        _ = await Assert.That(second.RootElement.GetProperty("input")[0].GetProperty("content")[0]
            .GetProperty("text").GetString()).IsEqualTo("next");
    }

    [Test]
    public async Task Turn_state_from_upgrade_is_sent_from_first_request_and_reset_without_losing_lineage(
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([
            Completed,
            Completed.Replace("resp-1", "resp-2", StringComparison.Ordinal),
            Completed.Replace("resp-1", "resp-3", StringComparison.Ordinal),
        ]);
        var connector = new ScriptedConnector([
            new ResponsesWebSocketOutcome(
                socket,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Codex-Turn-State"] = "upgrade-state",
                }),
        ]);
        using var handler = new EmptyHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));
        var secondRequest = new LLMRequest
        {
            Model = "model",
            Messages =
            [
                LLMMessage.User("one"),
                LLMMessage.Assistant("answer", []),
                LLMMessage.User("two"),
            ],
        };
        _ = await Drain(session.Call(secondRequest, cancellationToken));
        session.BeginTurn();
        var thirdRequest = new LLMRequest
        {
            Model = "model",
            Messages =
            [
                LLMMessage.User("one"),
                LLMMessage.Assistant("answer", []),
                LLMMessage.User("two"),
                LLMMessage.Assistant("answer", []),
                LLMMessage.User("three"),
            ],
        };
        _ = await Drain(session.Call(thirdRequest, cancellationToken));

        using var first = JsonDocument.Parse(socket.Sent[0]);
        using var second = JsonDocument.Parse(socket.Sent[1]);
        using var third = JsonDocument.Parse(socket.Sent[2]);
        _ = await Assert.That(first.RootElement.GetProperty("client_metadata")
            .GetProperty("x-codex-turn-state").GetString()).IsEqualTo("upgrade-state");
        _ = await Assert.That(second.RootElement.GetProperty("client_metadata")
            .GetProperty("x-codex-turn-state").GetString()).IsEqualTo("upgrade-state");
        _ = await Assert.That(third.RootElement.TryGetProperty("client_metadata", out _)).IsFalse();
        _ = await Assert.That(third.RootElement.GetProperty("previous_response_id").GetString()).IsEqualTo("resp-2");
    }

    [Test]
    public async Task Turn_state_from_metadata_is_case_insensitive_and_write_once(CancellationToken cancellationToken)
    {
        const string firstState = "{\"type\":\"response.metadata\",\"headers\":{\"X-Codex-Turn-State\":\"metadata-state\"}}";
        const string laterState = "{\"type\":\"response.metadata\",\"headers\":{\"x-codex-turn-state\":\"later-state\"}}";
        using var socket = new ScriptedWebSocket([
            firstState,
            laterState,
            Completed,
            Completed.Replace("resp-1", "resp-2", StringComparison.Ordinal),
        ]);
        var connector = new ScriptedConnector([socket]);
        using var handler = new EmptyHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));
        var secondRequest = new LLMRequest
        {
            Model = "model",
            Messages =
            [
                LLMMessage.User("one"),
                LLMMessage.Assistant("answer", []),
                LLMMessage.User("two"),
            ],
        };
        _ = await Drain(session.Call(secondRequest, cancellationToken));

        using var first = JsonDocument.Parse(socket.Sent[0]);
        using var second = JsonDocument.Parse(socket.Sent[1]);
        _ = await Assert.That(first.RootElement.TryGetProperty("client_metadata", out _)).IsFalse();
        _ = await Assert.That(second.RootElement.GetProperty("client_metadata")
            .GetProperty("x-codex-turn-state").GetString()).IsEqualTo("metadata-state");
    }

    [Test]
    [Arguments("changed-model", "same", 1)]
    [Arguments("model", "changed", 1)]
    [Arguments("model", "same", 2)]
    public async Task Changed_non_input_properties_send_a_full_request(
        string model,
        string instructions,
        int maxTokens,
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([Completed, Completed]);
        var connector = new ScriptedConnector([socket]);
        using var handler = new EmptyHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();
        var first = new LLMRequest
        {
            Model = "model",
            Messages = [LLMMessage.User("hello")],
            Instructions = "same",
            MaxTokens = 1,
        };
        var second = new LLMRequest
        {
            Model = model,
            Messages = [
                LLMMessage.User("hello"),
                LLMMessage.Assistant("answer", []),
                LLMMessage.User("next"),
            ],
            Instructions = instructions,
            MaxTokens = maxTokens,
        };

        _ = await Drain(session.Call(first, cancellationToken));
        _ = await Drain(session.Call(second, cancellationToken));

        using var sent = JsonDocument.Parse(socket.Sent[1]);
        _ = await Assert.That(sent.RootElement.TryGetProperty("previous_response_id", out _)).IsFalse();
        _ = await Assert.That(sent.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(3);
    }

    [Test]
    [Arguments("previous_response_not_found")]
    [Arguments("websocket_connection_limit_reached")]
    public async Task Named_v2_errors_retry_once_on_a_fresh_socket_with_a_full_request(
        string errorCode,
        CancellationToken cancellationToken)
    {
        using var failed = new ScriptedWebSocket([$"{{\"type\":\"error\",\"error\":{{\"code\":\"{errorCode}\",\"message\":\"retry\"}}}}"]);
        using var recovered = new ScriptedWebSocket([Completed]);
        var connector = new ScriptedConnector([failed, recovered]);
        using var handler = new EmptyHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        var events = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("hello")] }, cancellationToken));

        using var sent = JsonDocument.Parse(recovered.Sent.Single());
        _ = await Assert.That(connector.Calls).IsEqualTo(2);
        _ = await Assert.That(failed.Aborted).IsTrue();
        _ = await Assert.That(sent.RootElement.TryGetProperty("previous_response_id", out _)).IsFalse();
        _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);
    }

    [Test]
    public async Task Unsupported_upgrade_falls_back_to_http_and_is_sticky(CancellationToken cancellationToken)
    {
        var connector = new ScriptedConnector([new ResponsesWebSocketUpgradeException(404, "missing", new IOException())]);
        using var handler = new ResponsesHandler(2);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));
        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("two")] }, cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(handler.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Websocket_is_disabled_by_default(
        CancellationToken cancellationToken)
    {
        var connector = new ScriptedConnector([]);
        using var handler = new ResponsesHandler(2);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));
        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("two")] }, cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(0);
        _ = await Assert.That(handler.Calls).IsEqualTo(2);
    }

    [Test]
    public async Task Http_fallback_sends_and_captures_turn_state_header(CancellationToken cancellationToken)
    {
        var connector = new ScriptedConnector([new ResponsesWebSocketUpgradeException(404, "missing", new IOException())]);
        using var handler = new TurnStateResponsesHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));
        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("two")] }, cancellationToken));

        _ = await Assert.That(handler.Calls).IsEqualTo(2);
        _ = await Assert.That(handler.RequestHeaders[0].ContainsKey("x-codex-turn-state")).IsFalse();
        _ = await Assert.That(handler.RequestHeaders[1]["x-codex-turn-state"]).IsEqualTo("http-state");
    }

    [Test]
    [Timeout(10_000)]
    public async Task Exhausted_transport_retries_fall_back_once_and_remain_isolated(
        CancellationToken cancellationToken)
    {
        using var independentSocket = new ScriptedWebSocket([Completed]);
        var connector = new ScriptedConnector([
            .. Enumerable.Range(0, 6)
                .Select(_ => (object)new ResponsesWebSocketTransportException("dropped")),
            independentSocket,
        ]);
        using var handler = new ResponsesHandler(2);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new RetryingProvider(new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector));
        await using var failedSession = provider.OpenSession();
        await using var independentSession = provider.OpenSession();

        _ = await Drain(failedSession.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));
        _ = await Drain(failedSession.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("two")] }, cancellationToken));
        _ = await Drain(independentSession.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("three")] }, cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(7);
        _ = await Assert.That(handler.Calls).IsEqualTo(2);
        _ = await Assert.That(independentSocket.Sent).HasSingleItem();
    }

    [Test]
    public async Task Abandoned_response_is_poisoned_before_the_next_call(CancellationToken cancellationToken)
    {
        const string delta = "{\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}";
        using var abandoned = new ScriptedWebSocket([delta, Completed]);
        using var next = new ScriptedWebSocket([Completed.Replace("resp-1", "resp-2", StringComparison.Ordinal)]);
        var connector = new ScriptedConnector([abandoned, next]);
        using var handler = new EmptyHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        await using (var enumerator = session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken)
            .GetAsyncEnumerator(cancellationToken))
        {
            _ = await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
            _ = await Assert.That(enumerator.Current.Text).IsEqualTo("partial");
        }

        var events = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("two")] }, cancellationToken));

        _ = await Assert.That(abandoned.Aborted).IsTrue();
        _ = await Assert.That(connector.Calls).IsEqualTo(2);
        _ = await Assert.That(next.Sent).HasSingleItem();
        _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);
    }

    [Test]
    public async Task Cancellation_after_visible_output_keeps_websocket_enabled_for_the_next_call(
        CancellationToken cancellationToken)
    {
        const string delta = "{\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}";
        using var cancelledSocket = new ScriptedWebSocket([delta]);
        using var next = new ScriptedWebSocket([Completed]);
        var connector = new ScriptedConnector([cancelledSocket, next]);
        using var handler = new EmptyHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector);
        await using var session = provider.OpenSession();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var enumerator = session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancelled.Token)
            .GetAsyncEnumerator(cancelled.Token);

        _ = await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await cancelled.CancelAsync();
        _ = await Assert.That(async () => await enumerator.MoveNextAsync()).Throws<OperationCanceledException>();
        var events = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("two")] }, cancellationToken));

        _ = await Assert.That(cancelledSocket.Aborted).IsTrue();
        _ = await Assert.That(connector.Calls).IsEqualTo(2);
        _ = await Assert.That(next.Sent).HasSingleItem();
        _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);
    }

    [Test]
    [Timeout(15_000)]
    public async Task Http_fallback_receives_a_fresh_transport_retry_budget(CancellationToken cancellationToken)
    {
        var connector = new ScriptedConnector([
            .. Enumerable.Range(0, 6)
                .Select(_ => (object)new ResponsesWebSocketTransportException("dropped")),
        ]);
        using var handler = new TransientResponsesHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new RetryingProvider(new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector));
        await using var session = provider.OpenSession();

        var events = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));

        _ = await Assert.That(connector.Calls).IsEqualTo(6);
        _ = await Assert.That(handler.Calls).IsEqualTo(2);
        _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);
    }

    [Test]
    public async Task A_failure_after_visible_output_is_not_replayed_and_later_calls_use_http(
        CancellationToken cancellationToken)
    {
        const string delta = "{\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}";
        const string failed = "{\"type\":\"error\",\"error\":{\"code\":\"broken\",\"message\":\"no\"}}";
        using var socket = new ScriptedWebSocket([delta, failed]);
        var connector = new ScriptedConnector([socket]);
        using var handler = new ResponsesHandler(1);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new RetryingProvider(new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = false,
            },
            client,
            connector));
        await using var session = provider.OpenSession();
        var observed = new List<LLMEvent>();

        async Task First()
        {
            await foreach (var published in session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken))
            {
                observed.Add(published);
            }
        }

        _ = await Assert.That(First).Throws<ProviderResponseException>();
        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("two")] }, cancellationToken));
        _ = await Assert.That(observed).HasSingleItem();
        _ = await Assert.That(observed[0].Text).IsEqualTo("partial");
        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(handler.Calls).IsEqualTo(1);
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

    private sealed class FixedApiKeySource : IApiKeySource
    {
        public ValueTask<string> ApiKey(CancellationToken cancellationToken) => ValueTask.FromResult("key");

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed record ResponsesWebSocketOutcome(
        WebSocket Socket,
        IReadOnlyDictionary<string, string> ResponseHeaders);

    private sealed class ScriptedConnector(IEnumerable<object> outcomes) : IResponsesWebSocketConnector
    {
        private readonly Queue<object> _outcomes = new(outcomes);

        public int Calls { get; private set; }

        public Task<(WebSocket Socket, IReadOnlyDictionary<string, string> ResponseHeaders)> Connect(
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Calls++;
            var outcome = _outcomes.Dequeue();
            return outcome switch
            {
                WebSocket socket => Task.FromResult((socket, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))),
                ResponsesWebSocketOutcome connected => Task.FromResult((connected.Socket, connected.ResponseHeaders)),
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

        public bool Aborted { get; private set; }

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override WebSocketState State => _state;

        public override void Abort()
        {
            Aborted = true;
            _state = WebSocketState.Aborted;
        }

        public override Task CloseAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken)
        {
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(WebSocketCloseStatus closeStatus, string? statusDescription, CancellationToken cancellationToken) =>
            CloseAsync(closeStatus, statusDescription, cancellationToken);

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
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

    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP was not expected.");
    }

    private sealed class TurnStateResponsesHandler : HttpMessageHandler
    {
        public List<Dictionary<string, string>> RequestHeaders { get; } = [];

        public int Calls => RequestHeaders.Count;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var header in request.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            RequestHeaders.Add(headers);
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n",
                    Encoding.UTF8,
                    "text/event-stream"),
            };
            _ = response.Headers.TryAddWithoutValidation("X-Codex-Turn-State", "http-state");
            return Task.FromResult(response);
        }
    }

    private sealed class TransientResponsesHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Calls == 1
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                {
                    Content = new StringContent("transient", Encoding.UTF8, "text/plain"),
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n",
                        Encoding.UTF8,
                        "text/event-stream"),
                });
        }
    }

    private sealed class ResponsesHandler(int expectedCalls) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls > expectedCalls)
            {
                throw new InvalidOperationException("Too many HTTP requests.");
            }

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

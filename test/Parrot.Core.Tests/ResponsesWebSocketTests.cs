using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ResponsesWebSocketTests
{
    [Test]
    [Arguments("completed")]
    [Arguments("partial_cancelled")]
    [Arguments("partial_failed")]
    public async Task Attempt_bytes_count_utf8_fragments_including_partial_messages(string behavior, CancellationToken cancellationToken)
    {
        var payload = Encoding.UTF8.GetBytes("{\"type\":\"response.completed\",\"response\":{\"output\":[],\"id\":\"é\"}}");
        var frames = new List<ScriptedFrame> { new(payload[..11], WebSocketMessageType.Text, false) };
        if (behavior == "completed")
        {
            frames.Add(new(payload[11..], WebSocketMessageType.Text, true));
        }
        else if (behavior == "partial_failed")
        {
            frames.Add(new([], WebSocketMessageType.Close, true));
        }

        using var socket = new ScriptedWebSocket(frames);
        await using var connection = new ResponsesWebSocket(
            socket,
            new Dictionary<string, string>(StringComparer.Ordinal),
            TimeSpan.FromSeconds(1),
            1024);
        var attempt = new ProviderAttemptDiagnostics(
            TestDiagnosticLog.Instance,
            new Parrot.Diagnostics.DiagnosticEvent("provider", "request_started", Parrot.Diagnostics.DiagnosticSeverity.Information));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (behavior == "partial_cancelled")
        {
            cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));
        }

        Exception? failure = null;
        try
        {
            await foreach (var published in connection.Send(Encoding.UTF8.GetBytes("éé"), new ResponsesAdapter.ParseState(), attempt, cancellation.Token))
            {
                _ = published;
            }
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        _ = await Assert.That(failure is not null).IsEqualTo(behavior != "completed");
        _ = await Assert.That(attempt.RequestBytes).IsEqualTo(4);
        _ = await Assert.That(attempt.ResponseBytes).IsEqualTo(behavior == "completed" ? payload.Length : 11);
        _ = await Assert.That(socket.Aborted).IsEqualTo(behavior != "completed");
    }

    [Test]
    public async Task Client_derives_secure_endpoint_and_adds_v2_and_auth_headers(CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([]);
        var connector = new RecordingConnector(socket);
        var client = new ResponsesWebSocketClient(
            connector,
            new Uri("https://api.example.test/v1/responses"),
            TimeSpan.FromSeconds(10),
            ResponsesWebSocket.DefaultIdleTimeout,
            new Parrot.Config.RequestLimitsConfig().ProviderRequestBytes);

        await using var connection = await client.Connect(
            new Dictionary<string, string>(StringComparer.Ordinal) { ["Authorization"] = "Bearer key" },
            cancellationToken);

        _ = await Assert.That(connector.Endpoint).IsEqualTo(new Uri("wss://api.example.test/v1/responses"));
        _ = await Assert.That(connector.Headers["Authorization"]).IsEqualTo("Bearer key");
        _ = await Assert.That(connector.Headers["OpenAI-Beta"]).IsEqualTo("responses_websockets=2026-02-06");
        _ = await Assert.That(connector.Timeout).IsEqualTo(TimeSpan.FromSeconds(10));
    }

    [Test]
    public async Task Client_derives_plaintext_endpoint_and_certificate_exception_is_authority_scoped()
    {
        _ = await Assert.That(ResponsesWebSocketClient.Endpoint(
            new Uri("http://api.example.test/v1/responses"))).IsEqualTo(
            new Uri("ws://api.example.test/v1/responses"));
        using var configuredRequest = new HttpRequestMessage(
            HttpMethod.Get, "wss://api.example.test/v1/responses");
        using var redirectedRequest = new HttpRequestMessage(
            HttpMethod.Get, "wss://other.example.test/v1/responses");
        _ = await Assert.That(ResponsesWebSocketConnector.HasAuthority(
            configuredRequest, "https://api.example.test")).IsTrue();
        _ = await Assert.That(ResponsesWebSocketConnector.HasAuthority(
            redirectedRequest, "https://api.example.test")).IsFalse();
    }

    [Test]
    public async Task Text_frames_reassemble_and_share_responses_event_semantics(CancellationToken cancellationToken)
    {
        const string delta = "{\"type\":\"response.output_text.delta\",\"delta\":\"hello\"}";
        const string completed = "{\"type\":\"response.completed\",\"response\":{\"id\":\"resp-1\",\"usage\":{\"input_tokens\":7,\"output_tokens\":2},\"output\":[{\"type\":\"message\",\"role\":\"assistant\",\"content\":[{\"type\":\"output_text\",\"text\":\"hello\"}]}]}}";
        var split = Encoding.UTF8.GetBytes(delta);
        using var socket = new ScriptedWebSocket([
            new ScriptedFrame(split[..17], WebSocketMessageType.Text, false),
            new ScriptedFrame(split[17..], WebSocketMessageType.Text, true),
            new ScriptedFrame(Encoding.UTF8.GetBytes(completed), WebSocketMessageType.Text, true),
        ]);
        await using var connection = new ResponsesWebSocket(
            socket,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(1),
            new Parrot.Config.RequestLimitsConfig().ProviderRequestBytes);
        var request = ResponsesAdapter.Prepare(new LLMRequest
        {
            Model = "model",
            Messages = [LLMMessage.User("hello")],
        });
        var state = new ResponsesAdapter.ParseState();
        var events = new List<LLMEvent>();

        await foreach (var published in connection.Send(
            request.EncodeWebSocket(string.Empty, request.Input, string.Empty), state, null, cancellationToken))
        {
            events.Add(published);
        }

        using var sent = JsonDocument.Parse(socket.Sent);
        _ = await Assert.That(sent.RootElement.GetProperty("type").GetString()).IsEqualTo("response.create");
        _ = await Assert.That(sent.RootElement.GetProperty("stream").GetBoolean()).IsTrue();
        _ = await Assert.That(string.Join(",", events.Select(item => item.Kind)))
            .IsEqualTo("TextDelta,Completed");
        _ = await Assert.That(events[^1].AssistantText).IsEqualTo("hello");
        _ = await Assert.That(events[^1].InputTokens).IsEqualTo(7);
        _ = await Assert.That(state.ResponseId).IsEqualTo("resp-1");
        _ = await Assert.That(state.Output).HasSingleItem();
    }

    [Test]
    [Arguments(WebSocketMessageType.Binary, "unexpected binary")]
    [Arguments(WebSocketMessageType.Close, "closed before")]
    public async Task Invalid_transport_messages_abort_the_socket(
        WebSocketMessageType messageType,
        string expectedMessage,
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([new ScriptedFrame([], messageType, true)]);
        await using var connection = new ResponsesWebSocket(
            socket,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromSeconds(1),
            new Parrot.Config.RequestLimitsConfig().ProviderRequestBytes);
        var state = new ResponsesAdapter.ParseState();

        async Task Consume()
        {
            await foreach (var published in connection.Send(Encoding.UTF8.GetBytes("{}"), state, null, cancellationToken))
            {
                _ = published;
            }
        }

        _ = await Assert.That(Consume).Throws<WireProtocolException>().WithMessageContaining(expectedMessage);
        _ = await Assert.That(socket.Aborted).IsTrue();
    }

    [Test]
    public async Task Cancellation_aborts_a_pending_receive(CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([]);
        await using var connection = new ResponsesWebSocket(
            socket,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            TimeSpan.FromMinutes(1),
            new Parrot.Config.RequestLimitsConfig().ProviderRequestBytes);
        var state = new ResponsesAdapter.ParseState();
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cancelled.CancelAfter(TimeSpan.FromMilliseconds(10));

        async Task Consume()
        {
            await foreach (var published in connection.Send(Encoding.UTF8.GetBytes("{}"), state, null, cancelled.Token))
            {
                _ = published;
            }
        }

        _ = await Assert.That(Consume).Throws<OperationCanceledException>();
        _ = await Assert.That(socket.Aborted).IsTrue();
    }

    [Test]
    [Arguments(3, false)]
    [Arguments(4, true)]
    [Arguments(5, true)]
    public async Task Request_limit_counts_encoded_bytes_before_sending(
        int maximumRequestBytes,
        bool accepted,
        CancellationToken cancellationToken)
    {
        var request = Encoding.UTF8.GetBytes("éé");
        using var socket = new ScriptedWebSocket([
            new ScriptedFrame(Encoding.UTF8.GetBytes("{\"type\":\"response.completed\",\"response\":{\"output\":[]}}"), WebSocketMessageType.Text, true),
        ]);
        await using var connection = new ResponsesWebSocket(
            socket,
            new Dictionary<string, string>(StringComparer.Ordinal),
            TimeSpan.FromSeconds(1),
            maximumRequestBytes);

        async Task Consume()
        {
            await foreach (var published in connection.Send(request, new ResponsesAdapter.ParseState(), null, cancellationToken))
            {
                _ = published;
            }
        }

        if (accepted)
        {
            await Consume();
        }
        else
        {
            _ = await Assert.That(Consume).Throws<ProviderHttpException>().WithMessageContaining("exceeds 3 bytes");
        }

        _ = await Assert.That(socket.Sent.Length).IsEqualTo(accepted ? request.Length : 0);
    }

    [Test]
    [Arguments(3, false)]
    [Arguments(4, true)]
    [Arguments(5, true)]
    public async Task Http_request_limit_counts_encoded_bytes_before_sending(
        int maximumRequestBytes,
        bool accepted,
        CancellationToken cancellationToken)
    {
        using var handler = new RecordingHttpHandler();
        using var client = new HttpClient(handler, disposeHandler: false);

        async Task Send()
        {
            await using var response = await HttpStreaming.OpenStream(
                client,
                new Uri("https://example.test/responses"),
                Encoding.UTF8.GetBytes("éé"),
                new Dictionary<string, string>(StringComparer.Ordinal),
                TimeSpan.FromSeconds(1),
                maximumRequestBytes,
                null,
                cancellationToken);
        }

        if (accepted)
        {
            await Send();
        }
        else
        {
            _ = await Assert.That(Send).Throws<ProviderHttpException>().WithMessageContaining("exceeds 3 bytes");
        }

        _ = await Assert.That(handler.Calls).IsEqualTo(accepted ? 1 : 0);
    }

    private sealed class RecordingHttpHandler : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(string.Empty),
            });
        }
    }

    private sealed class RecordingConnector(WebSocket socket) : IResponsesWebSocketConnector
    {
        public Uri Endpoint { get; private set; } = new("wss://unset.invalid");

        public IReadOnlyDictionary<string, string> Headers { get; private set; } =
            new Dictionary<string, string>(StringComparer.Ordinal);

        public TimeSpan Timeout { get; private set; }

        public Task<(WebSocket Socket, IReadOnlyDictionary<string, string> ResponseHeaders)> Connect(
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            Endpoint = endpoint;
            Headers = headers;
            Timeout = timeout;
            return Task.FromResult((socket, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
        }
    }

    private sealed record ScriptedFrame(byte[] Bytes, WebSocketMessageType Type, bool End);

    private sealed class ScriptedWebSocket(IEnumerable<ScriptedFrame> frames) : WebSocket
    {
        private readonly Queue<ScriptedFrame> _frames = new(frames);
        private WebSocketState _state = WebSocketState.Open;

        public byte[] Sent { get; private set; } = [];

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

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            if (_frames.TryDequeue(out var frame))
            {
                frame.Bytes.AsSpan().CopyTo(buffer.AsSpan());
                return Task.FromResult(new WebSocketReceiveResult(frame.Bytes.Length, frame.Type, frame.End));
            }

            return WaitForCancellation(cancellationToken);
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken)
        {
            Sent = [.. buffer];
            return Task.CompletedTask;
        }

        private static async Task<WebSocketReceiveResult> WaitForCancellation(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("An infinite delay completed without cancellation.");
        }
    }
}

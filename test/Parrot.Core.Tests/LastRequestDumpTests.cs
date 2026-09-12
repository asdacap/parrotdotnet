using System.Net;
using System.Net.WebSockets;
using System.Text;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Llm.Wire;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class LastRequestDumpTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "parrot-last-request-dump-tests", Guid.NewGuid().ToString("N"));
    private readonly AgentScratchDirectory _scratch;
    private readonly string _dumpedPath;

    public LastRequestDumpTests()
    {
        _ = Directory.CreateDirectory(_root);
        _scratch = new AgentScratchDirectory(_root);
        _dumpedPath = Path.Combine(_scratch.Root, "last_request.json");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    [Arguments(CompatibleProtocol.ChatCompletions)]
    [Arguments(CompatibleProtocol.Responses)]
    public async Task Http_requests_replace_last_request_file_with_the_wire_body(
        CompatibleProtocol protocol,
        CancellationToken cancellationToken)
    {
        using var handler = new SseCompletionHandler(protocol);
        using var client = new HttpClient(handler, disposeHandler: false);
        var sessions = new ProviderSessions(TestDiagnosticLog.Instance, "dump-agent", OpenDumper());

        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "provider",
                BaseUrl = "https://example.test/v1",
                Protocol = protocol,
                ApiKeySource = new FixedKeySource(),
            },
            client);

        var session = sessions.Get(provider);
        var firstRequest = new LLMRequest { Model = "model", Messages = [LLMMessage.User("first message")] };
        var firstEvents = await Drain(session.Call(firstRequest, cancellationToken), cancellationToken);
        _ = await Assert.That(firstEvents[^1].Kind).IsEqualTo(LLMEventKind.Completed);
        var firstBody = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_dumpedPath, cancellationToken));
        _ = await Assert.That(handler.SentRequests.Count).IsEqualTo(1);
        _ = await Assert.That(firstBody).IsEqualTo(handler.SentRequests[0]);

        var secondRequest = new LLMRequest { Model = "model", Messages = [LLMMessage.User("second message")] };
        var secondEvents = await Drain(session.Call(secondRequest, cancellationToken), cancellationToken);
        _ = await Assert.That(secondEvents[^1].Kind).IsEqualTo(LLMEventKind.Completed);
        var secondBody = await File.ReadAllTextAsync(_dumpedPath, cancellationToken);
        _ = await Assert.That(handler.SentRequests.Count).IsEqualTo(2);
        _ = await Assert.That(secondBody).IsEqualTo(handler.SentRequests[1]);
        _ = await Assert.That(secondBody.Contains("second message", StringComparison.Ordinal)).IsTrue();
        _ = await Assert.That(secondBody.Contains("first message", StringComparison.Ordinal)).IsFalse();
        _ = await Assert.That(Directory.GetFiles(_scratch.Root).Length).IsEqualTo(1);
        await sessions.Close().ConfigureAwait(false);
    }

    [Test]
    public async Task WebSocket_frames_replace_last_request_file_with_the_sent_bytes(
        CancellationToken cancellationToken)
    {
        const string completed = """
            {"type":"response.completed","response":{"id":"resp-1","usage":{"input_tokens":1,"output_tokens":1},"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}}
            """;
        using var socket = new SentWebSocket([completed]);
        using var handler = new RejectingHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        var sessions = new ProviderSessions(TestDiagnosticLog.Instance, "dump-agent", OpenDumper());

        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "provider",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedKeySource(),
                DisableWebSocket = false,
            },
            client,
            new SingleConnector(socket));

        var session = sessions.Get(provider);
        var request = new LLMRequest { Model = "model", Messages = [LLMMessage.User("greeting")] };
        var events = await Drain(session.Call(request, cancellationToken), cancellationToken);
        _ = await Assert.That(events[^1].Kind).IsEqualTo(LLMEventKind.Completed);

        _ = await Assert.That(socket.Sent.Count > 0).IsTrue();
        var expected = Encoding.UTF8.GetString(socket.Sent[^1]);
        var dumped = await File.ReadAllTextAsync(_dumpedPath, cancellationToken);
        _ = await Assert.That(dumped).IsEqualTo(expected);
        _ = await Assert.That(dumped.Contains("greeting", StringComparison.Ordinal)).IsTrue();
        await sessions.Close().ConfigureAwait(false);
    }

    [Test]
    public async Task Compaction_requests_dump_through_the_agents_provider_sessions(
        CancellationToken cancellationToken)
    {
        using var handler = new SseCompletionHandler(CompatibleProtocol.ChatCompletions);
        using var client = new HttpClient(handler, disposeHandler: false);
        var sessions = new ProviderSessions(TestDiagnosticLog.Instance, "dump-agent", OpenDumper());

        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "provider",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.ChatCompletions,
                ApiKeySource = new FixedKeySource(),
            },
            client);

        var model = new ProviderModel(provider, new LLMModel("model", provider.Id) { ContextWindow = 100_000, MaxInputTokens = 100_000 });
        var selection = new ResolvedModelSelection(
            new ModelSelector(model.Selector),
            null,
            model,
            new ModelRoutingSnapshot(model.Selector, new ModelAliasSnapshot([]), 0));
        var compactor = new Compactor(1, 1, 60_000, 1024, TestModels.PromptTemplates);
        var oldGroupA = new CompactionGroup([LLMMessage.User(new string('a', 8000))], 1, false, true);
        var oldGroupB = new CompactionGroup([LLMMessage.User(new string('b', 8000))], 2, false, true);
        var recentGroup = new CompactionGroup([LLMMessage.User("recent")], 3, false, true);
        var groups = new List<CompactionGroup> { oldGroupA, oldGroupB, recentGroup };
        _ = await compactor.CompactWithProviderSessions(
            selection,
            ContextSize.Parse("1"),
            string.Empty,
            [],
            groups,
            0,
            LLMMessage.User("fixed"),
            new CompactionGroupBlobStore(_scratch),
            sessions,
            static (_, _) => ValueTask.CompletedTask,
            cancellationToken);

        _ = await Assert.That(handler.SentRequests.Count > 0).IsTrue();
        var lastBody = Encoding.UTF8.GetString(await File.ReadAllBytesAsync(_dumpedPath, cancellationToken));
        _ = await Assert.That(lastBody).IsEqualTo(handler.SentRequests[^1]);
        await sessions.Close().ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<LLMEvent>> Drain(
        IAsyncEnumerable<LLMEvent> events,
        CancellationToken cancellationToken)
    {
        var collected = new List<LLMEvent>();
        await foreach (var published in events.WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            collected.Add(published);
        }

        return collected;
    }

    private LastRequestDumper OpenDumper() => new(_dumpedPath, TestDiagnosticLog.Instance);

    private sealed class FixedKeySource : IApiKeySource
    {
        public ValueTask<string> ApiKey(CancellationToken cancellationToken) => ValueTask.FromResult("key");

        public ValueTask<bool> HasCredential(CancellationToken cancellationToken) => ValueTask.FromResult(true);
    }

    private sealed class SseCompletionHandler(CompatibleProtocol protocol) : HttpMessageHandler
    {
        private const string ChatCompletionsCompleted =
            "data: {\"choices\":[{\"index\":0,\"finish_reason\":null,\"delta\":{\"content\":\"summary\"}}]}\n\ndata: {\"choices\":[{\"index\":0,\"finish_reason\":\"stop\",\"delta\":{}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1}}\n\ndata: [DONE]\n\n";

        private const string ResponsesCompleted =
            "data: {\"type\":\"response.completed\",\"response\":{\"usage\":{\"input_tokens\":1,\"output_tokens\":1}}}\n\n";

        public List<string> SentRequests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = request.Content ?? throw new InvalidOperationException("No request content.");
            var body = await content.ReadAsStringAsync(cancellationToken);
            SentRequests.Add(body);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    protocol == CompatibleProtocol.ChatCompletions ? ChatCompletionsCompleted : ResponsesCompleted,
                    Encoding.UTF8,
                    "text/event-stream"),
            };
        }
    }

    private sealed class RejectingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("HTTP was not expected.");
    }

    private sealed class SingleConnector(WebSocket socket) : IResponsesWebSocketConnector
    {
        public Task<(WebSocket Socket, IReadOnlyDictionary<string, string> ResponseHeaders)> Connect(
            Uri endpoint,
            IReadOnlyDictionary<string, string> headers,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            Task.FromResult((socket, (IReadOnlyDictionary<string, string>)new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)));
    }

    private sealed class SentWebSocket(IEnumerable<string> responses) : WebSocket
    {
        private readonly Queue<byte[]> _responses = new(responses.Select(Encoding.UTF8.GetBytes));
        private WebSocketState _state = WebSocketState.Open;

        public List<byte[]> Sent { get; } = [];

        public override WebSocketCloseStatus? CloseStatus => null;

        public override string? CloseStatusDescription => null;

        public override string? SubProtocol => null;

        public override WebSocketState State => _state;

        public override void Abort() => _state = WebSocketState.Aborted;

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

            return Task.FromException<WebSocketReceiveResult>(new InvalidOperationException("No scripted response left."));
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
    }
}

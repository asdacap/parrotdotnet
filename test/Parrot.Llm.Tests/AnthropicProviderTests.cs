using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Parrot.Auth;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class AnthropicProviderTests
{
    private static LLMRequest Hello { get; } = new()
    {
        Model = "claude-opus-5-5",
        MaxOutputTokens = 1000,
        Messages = [LLMMessage.User("hello")],
    };

    [Test]
    public async Task Call_encodes_the_history_and_assembles_the_streamed_message(CancellationToken cancellationToken)
    {
        const string stream = """
            event: message_start
            data: {"type":"message_start","message":{"id":"msg_1","usage":{"input_tokens":10,"cache_read_input_tokens":100,"cache_creation_input_tokens":5,"output_tokens":1}}}

            event: content_block_start
            data: {"type":"content_block_start","index":0,"content_block":{"type":"thinking","thinking":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"thinking_delta","thinking":"Planning."}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":0,"delta":{"type":"signature_delta","signature":"sig"}}

            event: content_block_stop
            data: {"type":"content_block_stop","index":0}

            event: ping
            data: {"type":"ping"}

            event: content_block_start
            data: {"type":"content_block_start","index":1,"content_block":{"type":"text","text":""}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"Hel"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":1,"delta":{"type":"text_delta","text":"lo"}}

            event: content_block_start
            data: {"type":"content_block_start","index":2,"content_block":{"type":"tool_use","id":"call-3","name":"read","input":{}}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"{\"path\":"}}

            event: content_block_delta
            data: {"type":"content_block_delta","index":2,"delta":{"type":"input_json_delta","partial_json":"\"b\"}"}}

            event: content_block_start
            data: {"type":"content_block_start","index":3,"content_block":{"type":"tool_use","id":"call-4","name":"list","input":{}}}

            event: message_delta
            data: {"type":"message_delta","delta":{"stop_reason":"tool_use"},"usage":{"output_tokens":7}}

            event: message_stop
            data: {"type":"message_stop"}


            """;
        using var handler = new RecordingHttpHandler(_ => EventStream(stream));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = await BuildProvider(client, cancellationToken);
        var request = new LLMRequest
        {
            Model = "claude-opus-5-5",
            MaxOutputTokens = 1000,
            Instructions = "Be brief.",
            Reasoning = new ReasoningOptions("high", "auto"),
            Tools = [new LLMToolDefinition("read", "Reads a file.", """{"type":"object"}""")],
            Messages =
            [
                LLMMessage.User([
                    LLMContent.TextPart("Look"),
                    LLMContent.ImageLink("unused.png", "https://example.test/a.png", "image/png", 1, 1),
                ]),
                LLMMessage.Assistant("Reading.", [new LLMToolCall("call-1", "read", """{"path":"a"}""")]),
                LLMMessage.ToolResult("call-1", "file contents"),
                LLMMessage.System("Time is up."),
                LLMMessage.Assistant(string.Empty, [new LLMToolCall("call-2", "read", "{broken")]),
                LLMMessage.ToolResult("call-2", string.Empty),
            ],
        };

        var events = await Drain(provider.Call(request, cancellationToken));

        const string expectedBody = """
            {
              "model": "claude-opus-5-5",
              "max_tokens": 1000,
              "system": [{ "type": "text", "text": "Be brief.", "cache_control": { "type": "ephemeral" } }],
              "messages": [
                { "role": "user", "content": [
                  { "type": "text", "text": "Look" },
                  { "type": "image", "source": { "type": "url", "url": "https://example.test/a.png" } } ] },
                { "role": "assistant", "content": [
                  { "type": "text", "text": "Reading." },
                  { "type": "tool_use", "id": "call-1", "name": "read", "input": { "path": "a" } } ] },
                { "role": "user", "content": [
                  { "type": "tool_result", "tool_use_id": "call-1", "content": [{ "type": "text", "text": "file contents" }] },
                  { "type": "text", "text": "<system-update>\nTime is up.\n</system-update>" } ] },
                { "role": "assistant", "content": [{ "type": "tool_use", "id": "call-2", "name": "read", "input": {} }] },
                { "role": "user", "content": [{ "type": "tool_result", "tool_use_id": "call-2" }] }
              ],
              "tools": [{ "name": "read", "description": "Reads a file.", "input_schema": { "type": "object" } }],
              "stream": true,
              "thinking": { "type": "adaptive", "display": "summarized", "block_binding": { "prefix_mismatch_behavior": "drop_block" } },
              "output_config": { "effort": "high" },
              "cache_control": { "type": "ephemeral" }
            }
            """;
        var sent = handler.Requests.Single();
        _ = await Assert.That(sent.Uri.AbsoluteUri).IsEqualTo("https://example.test/v1/messages");
        _ = await Assert.That(sent.Header("x-api-key")).IsEqualTo("key");
        _ = await Assert.That(sent.Header("anthropic-version")).IsEqualTo("2023-06-01");
        _ = await Assert.That(sent.Header("anthropic-beta")).IsEqualTo("thinking-binding-controls-2026-08-01");
        _ = await Assert.That(sent.Header("authorization")).IsEqualTo(string.Empty);
        _ = await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(sent.Body), JsonNode.Parse(expectedBody))).IsTrue();

        var reasoning = events.Where(published => published.Kind == LLMEventKind.ReasoningDelta).ToList();
        _ = await Assert.That(string.Join("|", reasoning.Select(published => $"{published.Text}:{published.ReasoningPartId}:{published.ReasoningCompleted}")))
            .IsEqualTo("Planning.:msg_1:0:False|:msg_1:0:True");
        _ = await Assert.That(string.Join("|", events.Where(published => published.Kind == LLMEventKind.TextDelta).Select(published => published.Text)))
            .IsEqualTo("Hel|lo");
        var completed = events[^1];
        _ = await Assert.That(completed.Kind).IsEqualTo(LLMEventKind.Completed);
        _ = await Assert.That(completed.FinishReason).IsEqualTo("tool_calls");
        _ = await Assert.That(completed.InputTokens).IsEqualTo(115);
        _ = await Assert.That(completed.CachedInputTokens).IsEqualTo(100);
        _ = await Assert.That(completed.OutputTokens).IsEqualTo(7);
        _ = await Assert.That(completed.AssistantText).IsEqualTo("Hello");
        _ = await Assert.That(completed.ToolCalls).IsEquivalentTo(
            [new LLMToolCall("call-3", "read", """{"path":"b"}"""), new LLMToolCall("call-4", "list", "{}")]);
        const string expectedContent = """
            [
              { "type": "thinking", "thinking": "Planning.", "signature": "sig" },
              { "type": "text", "text": "Hello" },
              { "type": "tool_use", "id": "call-3", "name": "read", "input": { "path": "b" } },
              { "type": "tool_use", "id": "call-4", "name": "list", "input": {} }
            ]
            """;
        _ = await Assert.That(completed.ProviderContent.ProviderId).IsEqualTo("custom");
        _ = await Assert.That(JsonNode.DeepEquals(JsonNode.Parse(completed.ProviderContent.Content), JsonNode.Parse(expectedContent))).IsTrue();
    }

    [Test]
    [Arguments("custom", true)]
    [Arguments("other", false)]
    public async Task Provider_content_is_replayed_only_to_its_producer(
        string producer,
        bool replayed,
        CancellationToken cancellationToken)
    {
        const string stored = """
            [
              { "type": "thinking", "thinking": "Plan.", "signature": "sig" },
              { "type": "text", "text": "Read" },
              { "type": "tool_use", "id": "call-1", "name": "read", "input": { "path": "a" } },
              { "type": "text", "text": "ing." }
            ]
            """;
        const string rebuilt = """
            [
              { "type": "text", "text": "Reading." },
              { "type": "tool_use", "id": "call-1", "name": "read", "input": { "path": "a" } }
            ]
            """;
        const string stream = """
            data: {"type":"message_delta","delta":{"stop_reason":"end_turn"}}

            data: {"type":"message_stop"}


            """;
        using var handler = new RecordingHttpHandler(_ => EventStream(stream));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = await BuildProvider(client, cancellationToken);
        var request = Hello with
        {
            Messages =
            [
                LLMMessage.User("hi"),
                LLMMessage.Assistant("Reading.", [new LLMToolCall("call-1", "read", """{"path":"a"}""")]) with
                {
                    ProviderContent = new(producer, stored),
                },
                LLMMessage.ToolResult("call-1", "contents"),
            ],
        };

        _ = await Drain(provider.Call(request, cancellationToken));

        var body = JsonNode.Parse(handler.Requests.Single().Body);
        _ = await Assert.That(JsonNode.DeepEquals(body?["messages"]?[1]?["content"], JsonNode.Parse(replayed ? stored : rebuilt))).IsTrue();
        _ = await Assert.That(JsonNode.DeepEquals(
            body?["thinking"],
            replayed ? JsonNode.Parse("""{ "type": "adaptive", "display": "summarized", "block_binding": { "prefix_mismatch_behavior": "drop_block" } }""") : null)).IsTrue();
    }

    [Test]
    [Arguments("end_turn", "stop")]
    [Arguments("stop_sequence", "stop")]
    [Arguments("tool_use", "tool_calls")]
    [Arguments("max_tokens", "length")]
    [Arguments("model_context_window_exceeded", "length")]
    [Arguments("refusal", "content_filter")]
    [Arguments("pause_turn", "incomplete")]
    public async Task Stop_reason_maps_to_the_neutral_finish_reason(
        string stopReason,
        string finishReason,
        CancellationToken cancellationToken)
    {
        var stream = $$$"""
            data: {"type":"message_delta","delta":{"stop_reason":"{{{stopReason}}}"}}

            data: {"type":"message_stop"}


            """;
        using var handler = new RecordingHttpHandler(_ => EventStream(stream));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = await BuildProvider(client, cancellationToken);

        var events = await Drain(provider.Call(Hello, cancellationToken));

        _ = await Assert.That(events[^1].FinishReason).IsEqualTo(finishReason);
    }

    [Test]
    public async Task Stream_errors_and_truncation_fail_the_call(CancellationToken cancellationToken)
    {
        const string overloaded = """
            data: {"type":"error","error":{"type":"overloaded_error","message":"Overloaded"}}


            """;
        const string truncated = """
            data: {"type":"message_start","message":{"id":"msg_1"}}


            """;
        using var handler = new RecordingHttpHandler(recorded => EventStream(
            recorded.Body.Contains("first", StringComparison.Ordinal) ? overloaded : truncated));
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = await BuildProvider(client, cancellationToken);

        var failure = await Assert.That(async () => { _ = await Drain(provider.Call(Hello with { Model = "first" }, cancellationToken)); })
            .Throws<ProviderResponseException>();
        _ = await Assert.That(failure?.ErrorType).IsEqualTo("overloaded_error");
        _ = await Assert.That(async () => { _ = await Drain(provider.Call(Hello, cancellationToken)); }).Throws<WireProtocolException>();
    }

    [Test]
    public async Task List_models_requests_every_page_entry_with_anthropic_headers(CancellationToken cancellationToken)
    {
        const string models = """
            {"data":[{"type":"model","id":"claude-opus-5-5","display_name":"Claude Opus 5.5"},{"type":"model","id":"claude-haiku-5-5","display_name":"Claude Haiku 5.5"}],"has_more":false}
            """;
        using var handler = new RecordingHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(models, Encoding.UTF8, "application/json"),
        });
        using var client = new HttpClient(handler, disposeHandler: false);
        var provider = await BuildProvider(client, cancellationToken);

        var listed = await provider.ListModels(cancellationToken);

        var sent = handler.Requests.Single();
        _ = await Assert.That(sent.Uri.PathAndQuery).IsEqualTo("/v1/models?limit=1000");
        _ = await Assert.That(sent.Header("x-api-key")).IsEqualTo("key");
        _ = await Assert.That(sent.Header("anthropic-version")).IsEqualTo("2023-06-01");
        _ = await Assert.That(string.Join(",", listed.Select(model => model.Id))).IsEqualTo("claude-haiku-5-5,claude-opus-5-5");
    }

    // A custom provider id with type anthropic, so resolution is by type rather than by id.
    private static async Task<ILLMProvider> BuildProvider(HttpClient client, CancellationToken cancellationToken)
    {
        var store = new InMemoryCredentialStore();
        await store.Set("custom", Credential.ForApiKey("key"), cancellationToken);
        var config = new ProviderConfig { Type = "anthropic", BaseUrl = "https://example.test/v1" };
        return ProviderImplementations.Resolve("custom", config)
            .Build(new ProviderBuildContext("custom", config, store, client, new UnusedBrowser(), []))
            .Provider;
    }

    private static HttpResponseMessage EventStream(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, "text/event-stream") };

    private static async Task<List<LLMEvent>> Drain(IAsyncEnumerable<LLMEvent> events)
    {
        var drained = new List<LLMEvent>();
        await foreach (var published in events)
        {
            drained.Add(published);
        }

        return drained;
    }

    private sealed class UnusedBrowser : IBrowserOpener
    {
        public Task Open(string url, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Parrot.Llm;
using Parrot.Llm.Wire;
using TUnit.Core.Enums;

namespace Parrot.Core.Tests;

internal sealed class OpenAICompatibleProviderWebSocketTests
{
    private const string Completed = """
        {"type":"response.completed","response":{"id":"resp-1","usage":{"input_tokens":1,"output_tokens":1},"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}}
        """;

    private static readonly ImmediateTimeProvider Time = new();

    [Test]
    [Arguments("websocket_closed")]
    [Arguments("websocket")]
    [Arguments("wire_protocol")]
    [Arguments("websocket_upgrade")]
    [Arguments("websocket_transport")]
    [Arguments("request_too_large")]
    public async Task Transport_failure_diagnostics_classify_without_recording_exception_messages(
        string code,
        CancellationToken cancellationToken)
    {
        Exception failure = code switch
        {
            "websocket_closed" => new WebSocketException(WebSocketError.ConnectionClosedPrematurely, "private-sentinel"),
            "websocket" => new WebSocketException("private-sentinel"),
            "wire_protocol" => new WireProtocolException("private-sentinel"),
            "websocket_upgrade" => new ResponsesWebSocketUpgradeException(503, "private-sentinel", new IOException("private-sentinel")),
            "websocket_transport" => new ResponsesWebSocketTransportException("private-sentinel"),
            "request_too_large" => new ProviderRequestTooLargeException("private-sentinel"),
            _ => throw new InvalidOperationException("Unknown failure code."),
        };
        using var log = new ProviderRequestLog();
        var connector = new ScriptedConnector([failure]);
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
        var sessions = log.OpenSessions("websocket-agent");
        try
        {
            var session = sessions.Get(provider);
            async Task Consume() => _ = await Drain(session.Call(
                new LLMRequest { Model = "model", Messages = [LLMMessage.User("private-sentinel")] }, cancellationToken));
            if (code == "websocket_upgrade")
            {
                _ = await Assert.That(Consume).Throws<ResponsesWebSocketUpgradeException>();
            }
            else if (code == "request_too_large")
            {
                _ = await Assert.That(Consume).Throws<ProviderRequestTooLargeException>();
            }
            else
            {
                _ = await Assert.That(Consume).Throws<ResponsesWebSocketTransportException>();
            }
        }
        finally
        {
            await sessions.Close();
        }

        await log.AssertFailureCodes(code, code);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1250)]
    public async Task Configured_header_timeout_reaches_websocket_connector(
        int timeoutMilliseconds,
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([Completed]);
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
                HeaderTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
            },
            client,
            connector);
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
        var connector = new ScriptedConnector(fallback
            ? [new ResponsesWebSocketUpgradeException(404, "missing", new IOException())]
            : []);
        using var handler = new ResponsesHandler(1)
        {
            HeaderDelay = TimeSpan.FromMilliseconds(250),
        };
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = !fallback,
                HeaderTimeout = TimeSpan.FromMilliseconds(timeoutMilliseconds),
            },
            client,
            connector);
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
        var connector = new ScriptedConnector(behavior == "reuse"
            ? [recovered]
            : behavior == "fallback"
                ? [new ResponsesWebSocketUpgradeException(404, "private-sentinel-upgrade", new IOException("private-sentinel-exception"))]
                : [failed, recovered]);
        using var handler = new ResponsesHandler(1);
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
        if (behavior != "fallback")
        {
            long?[] responseBytes = behavior == "reuse"
                ? [Encoding.UTF8.GetByteCount(completed), Encoding.UTF8.GetByteCount(completed)]
                : [Encoding.UTF8.GetByteCount($"{{\"type\":\"error\",\"error\":{{\"code\":\"{behavior}\",\"message\":\"private-sentinel-error\"}}}}"), Encoding.UTF8.GetByteCount(completed)];
            await log.AssertByteCounts(
                "websocket-agent",
                behavior == "reuse" ? [.. recovered.Sent.Select(bytes => (long?)bytes.Length)] : [failed.Sent[0].Length, recovered.Sent[0].Length],
                responseBytes);
        }
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
        var connector = new ScriptedConnector(fallback
            ? [new ResponsesWebSocketUpgradeException(404, "missing", new IOException())]
            : [socket]);
        using var handler = new ResponsesHandler(0);
        using var client = new HttpClient(handler, disposeHandler: false);
        ILLMProvider provider = new OpenAICompatibleProvider(
            new OpenAICompatibleOptions
            {
                Id = "configured",
                BaseUrl = "https://example.test/v1",
                Protocol = CompatibleProtocol.Responses,
                ApiKeySource = new FixedApiKeySource(),
                DisableWebSocket = disableWebSocket,
                MaximumRequestBytes = 1,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        async Task Consume() => _ = await Drain(session.Call(
            new LLMRequest { Model = "model", Messages = [LLMMessage.User("hello")] }, cancellationToken));

        _ = await Assert.That(Consume).Throws<ProviderRequestTooLargeException>().WithMessageContaining("exceeds 1 bytes");

        _ = await Assert.That(handler.Calls).IsEqualTo(0);
        _ = await Assert.That(socket.Sent.Count).IsEqualTo(0);
    }

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
        _ = await Assert.That(connector.ConnectTimeout).IsEqualTo(TimeSpan.FromSeconds(60));
        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(socket.Sent).Count().IsEqualTo(2);
        _ = await Assert.That(second.RootElement.GetProperty("previous_response_id").GetString()).IsEqualTo("resp-1");
        _ = await Assert.That(second.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(1);
        _ = await Assert.That(second.RootElement.GetProperty("input")[0].GetProperty("content")[0]
            .GetProperty("text").GetString()).IsEqualTo("next");
    }

    [Test]
    [Timeout(10_000)]
    [Arguments("")]
    [Arguments("new-upgrade-state")]
    public async Task New_turn_replaces_idle_socket_and_lineage_but_preserves_reuse_within_turn(
        string newTurnState,
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([
            Completed,
            Completed.Replace("resp-1", "resp-2", StringComparison.Ordinal),
        ]);
        using var nextSocket = new ScriptedWebSocket([
            Completed.Replace("resp-1", "resp-3", StringComparison.Ordinal),
            Completed.Replace("resp-1", "resp-4", StringComparison.Ordinal),
        ]);
        var connector = new ScriptedConnector([
            new ResponsesWebSocketOutcome(
                socket,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Codex-Turn-State"] = "upgrade-state",
                }),
            new ResponsesWebSocketOutcome(
                nextSocket,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["X-Codex-Turn-State"] = newTurnState,
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
        _ = await Assert.That(socket.State).IsEqualTo(WebSocketState.Open);
        session.BeginTurn();
        session.BeginTurn();
        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(socket.Aborted).IsFalse();
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
        var thirdEvents = await Drain(session.Call(thirdRequest, cancellationToken));
        var fourthRequest = thirdRequest with
        {
            Messages = [.. thirdRequest.Messages, LLMMessage.Assistant("answer", []), LLMMessage.User("four")],
        };
        var fourthEvents = await Drain(session.Call(fourthRequest, cancellationToken));

        _ = await Assert.That(socket.Aborted).IsTrue();
        _ = await Assert.That(socket.State).IsEqualTo(WebSocketState.Closed);
        _ = await Assert.That(socket.Sent).Count().IsEqualTo(2);
        _ = await Assert.That(nextSocket.Sent).Count().IsEqualTo(2);
        _ = await Assert.That(connector.Calls).IsEqualTo(2);
        _ = await Assert.That(thirdEvents.Any(item => item.Kind == LLMEventKind.Retry)).IsFalse();
        _ = await Assert.That(fourthEvents.Any(item => item.Kind == LLMEventKind.Retry)).IsFalse();
        using var first = JsonDocument.Parse(socket.Sent[0]);
        using var second = JsonDocument.Parse(socket.Sent[1]);
        using var third = JsonDocument.Parse(nextSocket.Sent[0]);
        using var fourth = JsonDocument.Parse(nextSocket.Sent[1]);
        _ = await Assert.That(first.RootElement.GetProperty("client_metadata")
            .GetProperty("x-codex-turn-state").GetString()).IsEqualTo("upgrade-state");
        _ = await Assert.That(second.RootElement.GetProperty("client_metadata")
            .GetProperty("x-codex-turn-state").GetString()).IsEqualTo("upgrade-state");
        _ = await Assert.That(second.RootElement.GetProperty("previous_response_id").GetString()).IsEqualTo("resp-1");
        _ = await Assert.That(second.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(1);
        if (newTurnState.Length == 0)
        {
            _ = await Assert.That(third.RootElement.TryGetProperty("client_metadata", out _)).IsFalse();
        }
        else
        {
            _ = await Assert.That(third.RootElement.GetProperty("client_metadata")
                .GetProperty("x-codex-turn-state").GetString()).IsEqualTo(newTurnState);
        }

        _ = await Assert.That(third.RootElement.TryGetProperty("previous_response_id", out _)).IsFalse();
        _ = await Assert.That(third.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(5);
        _ = await Assert.That(fourth.RootElement.GetProperty("previous_response_id").GetString()).IsEqualTo("resp-3");
        _ = await Assert.That(fourth.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(1);
    }

    [Test]
    [Arguments(59, false)]
    [Arguments(60, true)]
    public async Task Socket_idle_past_reuse_limit_is_replaced_with_a_full_request_without_retry(
        int idleSeconds,
        bool replaced,
        CancellationToken cancellationToken)
    {
        using var socket = new ScriptedWebSocket([Completed, Completed.Replace("resp-1", "resp-2", StringComparison.Ordinal)]);
        using var nextSocket = new ScriptedWebSocket([Completed.Replace("resp-1", "resp-2", StringComparison.Ordinal)]);
        var connector = new ScriptedConnector([socket, nextSocket]);
        var time = new ControlledTimeProvider();
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
                TimeProvider = time,
            },
            client,
            connector);
        await using var session = provider.OpenSession();

        _ = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("hello")] }, cancellationToken));
        time.AdvanceClock(TimeSpan.FromSeconds(idleSeconds));
        var continuedRequest = new LLMRequest
        {
            Model = "model",
            Messages = [LLMMessage.User("hello"), LLMMessage.Assistant("answer", []), LLMMessage.User("next")],
        };
        var continuedEvents = await Drain(session.Call(continuedRequest, cancellationToken));

        using var continued = JsonDocument.Parse(replaced ? nextSocket.Sent[0] : socket.Sent[1]);
        _ = await Assert.That(connector.Calls).IsEqualTo(replaced ? 2 : 1);
        _ = await Assert.That(socket.Aborted).IsEqualTo(replaced);
        _ = await Assert.That(continuedEvents.Any(item => item.Kind == LLMEventKind.Retry)).IsFalse();
        _ = await Assert.That(continued.RootElement.TryGetProperty("previous_response_id", out _)).IsEqualTo(!replaced);
        _ = await Assert.That(continued.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(replaced ? 3 : 1);
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
            MaxOutputTokens = 1,
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
            MaxOutputTokens = maxTokens,
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
        using var failed = new ScriptedWebSocket([$"{{\"type\":\"error\",\"error\":{{\"code\":\"{errorCode}\",\"message\":\"private-sentinel\"}}}}"]);
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
        var retry = events.Single(published => published.Kind == LLMEventKind.Retry);
        _ = await Assert.That(retry.Attempt).IsEqualTo(1);
        _ = await Assert.That(retry.RetryAfter).IsEqualTo(TimeSpan.Zero);
        _ = await Assert.That(retry.Text).IsEqualTo(errorCode == "previous_response_not_found"
            ? "Previous provider response is unavailable. Retrying the full request on a new WebSocket connection."
            : "WebSocket connection limit reached. Retrying the full request on a new connection.");
    }

    [Test]
    [Arguments(404)]
    [Arguments(405)]
    [Arguments(426)]
    public async Task Unsupported_upgrade_falls_back_to_http_and_is_sticky(int statusCode, CancellationToken cancellationToken)
    {
        var connector = new ScriptedConnector([new ResponsesWebSocketUpgradeException(statusCode, "private-sentinel", new IOException())]);
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

        var first = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));
        session.BeginTurn();
        var second = await Drain(session.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("two")] }, cancellationToken));

        _ = await Assert.That(first.Single(published => published.Kind == LLMEventKind.Retry)).IsEqualTo(
            LLMEvent.Retry(1, TimeSpan.Zero, $"WebSocket upgrade is unsupported (HTTP {statusCode}). Retrying over HTTP."));
        _ = await Assert.That(second.Any(published => published.Kind == LLMEventKind.Retry)).IsFalse();

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
    [ExecutionPriority(Priority.High)]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Exhausted_transport_retries_fall_back_once_and_remain_isolated(
        bool upgradeFailure, CancellationToken cancellationToken)
    {
        using var independentSocket = new ScriptedWebSocket([Completed]);
        var connector = new ScriptedConnector([
            .. Enumerable.Range(0, 6)
                .Select(_ => upgradeFailure
                    ? (object)new ResponsesWebSocketUpgradeException(502, "private-sentinel", new IOException())
                    : new ResponsesWebSocketTransportException("private-sentinel")),
            independentSocket,
        ]);
        using var handler = new ResponsesHandler(2);
        using var client = new HttpClient(handler, disposeHandler: false);
        OpenAICompatibleProvider compatible = new(
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
        ILLMProvider provider = new RetryingProvider(compatible) { TimeProvider = Time };

        await using var failedSession = provider.OpenSession();
        await using var independentSession = provider.OpenSession();

        var events = await Drain(failedSession.Call(new LLMRequest { Model = "model", Messages = [LLMMessage.User("one")] }, cancellationToken));
        var retries = events.Where(published => published.Kind == LLMEventKind.Retry).ToArray();
        _ = await Assert.That(retries.Length).IsEqualTo(6);
        _ = await Assert.That(retries.Select(published => published.Attempt).SequenceEqual([1, 2, 3, 4, 5, 6])).IsTrue();
        _ = await Assert.That(retries.Select(published => published.RetryAfter.TotalMilliseconds).SequenceEqual([200, 400, 800, 1600, 3200, 0])).IsTrue();
        _ = await Assert.That(retries[^1].Text).IsEqualTo("WebSocket retry budget exhausted. Retrying over HTTP.");
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
    [ExecutionPriority(Priority.High)]
    public async Task Http_fallback_receives_a_fresh_transport_retry_budget(CancellationToken cancellationToken)
    {
        var connector = new ScriptedConnector([
            .. Enumerable.Range(0, 6)
                .Select(_ => (object)new ResponsesWebSocketTransportException("dropped")),
        ]);
        using var handler = new TransientResponsesHandler();
        using var client = new HttpClient(handler, disposeHandler: false);
        OpenAICompatibleProvider compatible = new(
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
        ILLMProvider provider = new RetryingProvider(compatible) { TimeProvider = Time };

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
        OpenAICompatibleProvider compatible = new(
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
        ILLMProvider provider = new RetryingProvider(compatible) { TimeProvider = Time };

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

    [Test]
    [Arguments("incremental")]
    [Arguments("partial-native")]
    [Arguments("reconnect")]
    [Arguments("recovery")]
    [Arguments("fallback")]
    [Arguments("model-change")]
    public async Task Native_reasoning_preserves_incremental_and_full_transport_history(
        string behavior, CancellationToken cancellationToken)
    {
        const string nativeOutput = """
            [{"id":"rs_1","type":"reasoning","summary":[{"type":"summary_text","text":"summary"}],"encrypted_content":"opaque-reasoning"},
             {"id":"msg_1","type":"message","role":"assistant","content":[{"type":"output_text","text":"answer","annotations":[]}]},
             {"id":"fc_1","type":"function_call","call_id":"call_1","name":"read","arguments":"{}","status":"completed"}]
            """;
        const string unavailable = """
            {"type":"error","error":{"code":"previous_response_not_found","message":"unavailable"}}
            """;
        using var native = JsonDocument.Parse(nativeOutput);
        var initialFrames = new List<string>
        {
            "{\"type\":\"response.output_text.delta\",\"delta\":\"answer\"}",
        };
        for (var index = 0; index < (behavior == "partial-native" ? 1 : native.RootElement.GetArrayLength()); index++)
        {
            initialFrames.Add($"{{\"type\":\"response.output_item.done\",\"output_index\":{index},\"item\":{native.RootElement[index].GetRawText()}}}");
        }

        if (behavior == "partial-native")
        {
            initialFrames.Add("{\"type\":\"response.output_item.added\",\"item\":{\"id\":\"fc_1\",\"type\":\"function_call\",\"call_id\":\"call_1\",\"name\":\"read\"}}");
            initialFrames.Add("{\"type\":\"response.function_call_arguments.done\",\"item_id\":\"fc_1\",\"call_id\":\"call_1\",\"arguments\":\"{}\"}");
        }

        var completedOutput = behavior == "partial-native" ? "[]" : nativeOutput;
        initialFrames.Add($"{{\"type\":\"response.completed\",\"response\":{{\"id\":\"resp-native\",\"output\":{completedOutput}}}}}");
        initialFrames.Add(behavior == "recovery" ? unavailable : Completed);
        using var socket = new ScriptedWebSocket(initialFrames);
        using var replacement = new ScriptedWebSocket([Completed]);
        var connector = new ScriptedConnector(behavior == "fallback"
            ? [socket, new ResponsesWebSocketUpgradeException(426, "unsupported", new IOException("upgrade"))]
            : [socket, replacement]);
        using var handler = new ResponsesHandler(behavior == "fallback" ? 1 : 0);
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
        var initial = new LLMRequest { Model = "model", Messages = [LLMMessage.User("hello")] };
        var initialEvents = await Drain(session.Call(initial, cancellationToken));
        var outcome = initialEvents[^1];
        _ = await Assert.That(outcome.ProviderContent.ProviderId).IsEqualTo("configured");
        _ = await Assert.That(outcome.AssistantText).IsEqualTo("answer");
        var continued = initial with
        {
            Model = behavior == "model-change" ? "other-model" : "model",
            Messages = [.. initial.Messages, LLMMessage.Assistant(outcome.AssistantText, outcome.ToolCalls) with
            {
                ProviderContent = outcome.ProviderContent,
            }, LLMMessage.ToolResult("call_1", "result"), LLMMessage.User("next")],
        };
        if (behavior is "reconnect" or "fallback")
        {
            session.BeginTurn();
        }

        var continuedEvents = await Drain(session.Call(continued, cancellationToken));
        _ = await Assert.That(continuedEvents[^1].Kind).IsEqualTo(LLMEventKind.Completed);
        if (behavior is "incremental" or "recovery" or "partial-native")
        {
            using var incremental = JsonDocument.Parse(socket.Sent[1]);
            _ = await Assert.That(incremental.RootElement.GetProperty("previous_response_id").GetString()).IsEqualTo("resp-native");
            _ = await Assert.That(incremental.RootElement.GetProperty("input").GetArrayLength()).IsEqualTo(2);
            _ = await Assert.That(incremental.RootElement.GetProperty("input")[0].GetProperty("type").GetString()).IsEqualTo("function_call_output");
            _ = await Assert.That(incremental.RootElement.GetProperty("input")[1].GetProperty("content")[0].GetProperty("text").GetString()).IsEqualTo("next");
        }

        var requestBytes = behavior switch
        {
            "recovery" or "reconnect" => replacement.Sent[0],
            "fallback" => Encoding.UTF8.GetBytes(handler.RequestBodies.Single()),
            _ => socket.Sent[1],
        };
        using var encoded = JsonDocument.Parse(requestBytes);
        _ = await Assert.That(encoded.RootElement.GetProperty("include")[0].GetString()).IsEqualTo("reasoning.encrypted_content");
        if (behavior is not ("incremental" or "partial-native"))
        {
            _ = await Assert.That(encoded.RootElement.TryGetProperty("previous_response_id", out _)).IsFalse();
            var input = encoded.RootElement.GetProperty("input");
            _ = await Assert.That(input.GetArrayLength()).IsEqualTo(6);
            for (var index = 0; index < native.RootElement.GetArrayLength(); index++)
            {
                _ = await Assert.That(JsonElement.DeepEquals(input[index + 1], native.RootElement[index])).IsTrue();
            }
        }

        _ = await Assert.That(connector.Calls).IsEqualTo(behavior is "incremental" or "model-change" or "partial-native" ? 1 : 2);
        _ = await Assert.That(handler.Calls).IsEqualTo(behavior == "fallback" ? 1 : 0);
        _ = await Assert.That(continuedEvents.Count(item => item.Kind == LLMEventKind.Retry)).IsEqualTo(behavior is "recovery" or "fallback" ? 1 : 0);
    }

    [Test]
    public async Task Websocket_reasoning_rejection_is_not_retried_without_reasoning(CancellationToken cancellationToken)
    {
        const string rejection = """
            {"type":"error","error":{"code":"invalid_reasoning","message":"reasoning rejected"}}
            """;
        using var socket = new ScriptedWebSocket([rejection]);
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
        const string nativePayload = """
            {"Version":1,"Protocol":"responses","Output":[{"id":"rs_1","type":"reasoning","encrypted_content":"opaque-reasoning","summary":[]},{"type":"message","role":"assistant","content":[{"type":"output_text","text":"answer"}]}]}
            """;
        var request = new LLMRequest
        {
            Model = "model",
            Messages = [LLMMessage.Assistant("answer", []) with
            {
                ProviderContent = new LLMProviderContent("configured", nativePayload),
            }, LLMMessage.User("next")],
        };
        var events = new List<LLMEvent>();
        async Task Consume()
        {
            await foreach (var published in session.Call(request, cancellationToken))
            {
                events.Add(published);
            }
        }

        _ = await Assert.That(Consume).Throws<ProviderResponseException>();
        _ = await Assert.That(connector.Calls).IsEqualTo(1);
        _ = await Assert.That(socket.Sent.Count).IsEqualTo(1);
        _ = await Assert.That(events.Any(item => item.Kind == LLMEventKind.Retry)).IsFalse();
        using var encoded = JsonDocument.Parse(socket.Sent[0]);
        _ = await Assert.That(encoded.RootElement.GetProperty("input")[0].GetProperty("encrypted_content").GetString()).IsEqualTo("opaque-reasoning");
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
        public List<string> RequestBodies { get; } = [];

        public int Calls { get; private set; }

        public TimeSpan HeaderDelay { get; init; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Calls++;
            if (Calls > expectedCalls)
            {
                throw new InvalidOperationException("Too many HTTP requests.");
            }

            RequestBodies.Add(await (request.Content ?? throw new InvalidOperationException("Missing request body."))
                .ReadAsStringAsync(cancellationToken));
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

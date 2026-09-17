using System.Text;
using System.Text.Json;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Llm.Wire;
using Parrot.Tools;

namespace Parrot.Core.Tests;

internal sealed class OpenAICompatibleProviderTests
{
    // Captured verbatim from the live OpenCode Go endpoint: content and
    // reasoning_content arrive as separate fields on the same delta.
    private const string LiveShapedStream = """
        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"role":"assistant","content":"","reasoning_content":null}}],"usage":null}

        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"content":"","reasoning_content":"think"}}],"usage":null}

        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"content":"hello ","reasoning_content":null}}],"usage":null}

        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"content":"world","reasoning_content":null}}],"usage":null}

        data: {"choices":[{"index":0,"finish_reason":"stop","delta":{}}],"usage":{"prompt_tokens":11,"prompt_tokens_details":{"cached_tokens":5},"completion_tokens":3}}

        data: [DONE]

        """;

    private const string ToolCallStream = """
        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"exec_command","arguments":""}}]}}]}

        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"{\"command\": "}}]}}]}

        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"\"echo hi\"}"}}]}}]}

        data: {"choices":[{"index":0,"finish_reason":"tool_calls","delta":{}}],"usage":{"prompt_tokens":20,"completion_tokens":9}}

        data: [DONE]

        """;

    [Test]
    public async Task Encode_carries_configured_tool_schema_overrides_and_shipped_siblings(
        CancellationToken cancellationToken)
    {
        const string overriddenDescription = "Read configured content.";
        const string overriddenPathDescription = "Configured path prose.";
        const string shippedSiblingDescription =
            "Find paths beneath an optional root with deterministic glob matching, including **. Relative roots resolve within the workspace.";
        var directory = Path.Combine(Path.GetTempPath(), "parrot-chat-tool-definitions", Guid.NewGuid().ToString("N"));

        try
        {
            var overrideConfiguration = $"""
                tools:
                  read:
                    description: {overriddenDescription}
                    parameters:
                      properties:
                        path:
                          type: integer
                          minimum: 7
                          description: {overriddenPathDescription}
                """;
            var configurationPath = WriteConfiguration(directory, overrideConfiguration);
            var configuration = Configuration.Load(
                configurationPath,
                Path.Combine(directory, "predefined_config.yaml"));
            var tools = ToolSnapshot.Document(
                [new ReadTool(new ToolWorkspace(directory)), new GlobTool(new ToolWorkspace(directory))],
                [true, true],
                [configuration.ToolDefinitions.Describe("read"), configuration.ToolDefinitions.Describe("glob")]).Definitions;
            var request = new LLMRequest
            {
                Model = "vendor/model",
                Messages = [LLMMessage.User("hello")],
                Tools = tools,
            };

            using var document = JsonDocument.Parse(ChatCompletionsAdapter.Encode(request));
            var encodedTools = document.RootElement.GetProperty("tools");
            var read = encodedTools.EnumerateArray().Single(tool =>
                tool.GetProperty("function").GetProperty("name").GetString() == "read").GetProperty("function");
            var glob = encodedTools.EnumerateArray().Single(tool =>
                tool.GetProperty("function").GetProperty("name").GetString() == "glob").GetProperty("function");

            _ = await Assert.That(read.GetProperty("description").GetString()).IsEqualTo(overriddenDescription);
            _ = await Assert.That(read.GetProperty("parameters").GetProperty("properties").GetProperty("path")
                .GetProperty("description").GetString()).IsEqualTo(overriddenPathDescription);
            _ = await Assert.That(read.GetProperty("parameters").GetProperty("properties").GetProperty("path")
                .GetProperty("type").GetString()).IsEqualTo("integer");
            _ = await Assert.That(read.GetProperty("parameters").GetProperty("properties").GetProperty("path")
                .GetProperty("minimum").GetInt32()).IsEqualTo(7);
            _ = await Assert.That(glob.GetProperty("description").GetString()).IsEqualTo(shippedSiblingDescription);
            _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Test]
    public async Task Encode_writes_the_output_budget_as_max_tokens()
    {
        var request = new LLMRequest
        {
            Model = "vendor/model",
            Messages = [LLMMessage.User("hello")],
            MaxOutputTokens = 4096,
        };
        using var document = JsonDocument.Parse(ChatCompletionsAdapter.Encode(request));
        var root = document.RootElement;

        _ = await Assert.That(root.TryGetProperty("max_tokens", out var maxTokens)).IsTrue();
        _ = await Assert.That(root.TryGetProperty("max_completion_tokens", out _)).IsFalse();
        _ = await Assert.That(maxTokens.GetInt32()).IsEqualTo(4096);
    }

    [Test]
    [Arguments("xhigh", true)]
    [Arguments("", false)]
    public async Task Encode_writes_or_omits_top_level_reasoning_effort(
        string effort, bool hasReasoning, CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = "vendor/model",
            Messages = [LLMMessage.User("hello")],
            Reasoning = hasReasoning ? new ReasoningOptions(effort, "auto") : null,
        };
        using var document = JsonDocument.Parse(ChatCompletionsAdapter.Encode(request));
        var root = document.RootElement;

        _ = await Assert.That(root.TryGetProperty("reasoning_effort", out var encodedEffort)).IsEqualTo(hasReasoning);

        if (hasReasoning)
        {
            _ = await Assert.That(encodedEffort.GetString()).IsEqualTo(effort);
        }

        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    [Arguments("Follow the repository instructions.", 4)]
    [Arguments("", 3)]
    public async Task Encode_keeps_only_the_first_system_message_and_marks_later_ones(
        string instructions, int messageCount, CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = "vendor/model",
            Instructions = instructions,
            Messages =
            [
                LLMMessage.System("history guidance"),
                LLMMessage.User("hello"),
                LLMMessage.System("turn <reminder>"),
            ],
        };
        using var document = JsonDocument.Parse(ChatCompletionsAdapter.Encode(request));
        var root = document.RootElement;
        var messages = root.GetProperty("messages");

        _ = await Assert.That(root.TryGetProperty("instructions", out _)).IsFalse();
        _ = await Assert.That(messages.GetArrayLength()).IsEqualTo(messageCount);
        _ = await Assert.That(messages.EnumerateArray().Count(message =>
            message.GetProperty("role").GetString() == "system")).IsEqualTo(1);
        _ = await Assert.That(messages[0].GetProperty("role").GetString()).IsEqualTo("system");

        var historyIndex = 0;
        if (instructions.Length > 0)
        {
            _ = await Assert.That(messages[0].GetProperty("content").GetString()).IsEqualTo(instructions);
            _ = await Assert.That(messages[1].GetProperty("role").GetString()).IsEqualTo("user");
            _ = await Assert.That(messages[1].GetProperty("content").GetString())
                .IsEqualTo("<system-update>\nhistory guidance\n</system-update>");
            historyIndex = 1;
        }
        else
        {
            _ = await Assert.That(messages[0].GetProperty("content").GetString()).IsEqualTo("history guidance");
        }

        _ = await Assert.That(messages[historyIndex + 1].GetProperty("role").GetString()).IsEqualTo("user");
        _ = await Assert.That(messages[historyIndex + 1].GetProperty("content").GetString()).IsEqualTo("hello");
        _ = await Assert.That(messages[historyIndex + 2].GetProperty("role").GetString()).IsEqualTo("user");
        _ = await Assert.That(messages[historyIndex + 2].GetProperty("content").GetString())
            .IsEqualTo("<system-update>\nturn &lt;reminder&gt;\n</system-update>");
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Stream_ends_with_a_completed_event_carrying_the_outcome(CancellationToken cancellationToken)
    {
        var events = await Drain(cancellationToken);
        var completed = events[^1];

        _ = await Assert.That(completed.Kind).IsEqualTo(LLMEventKind.Completed);
        _ = await Assert.That(completed.FinishReason).IsEqualTo("stop");
        _ = await Assert.That(completed.InputTokens).IsEqualTo(11);
        _ = await Assert.That(completed.CachedInputTokens).IsEqualTo(5);
        _ = await Assert.That(completed.OutputTokens).IsEqualTo(3);
    }

    [Test]
    [Arguments(LLMEventKind.TextDelta, 2)]
    [Arguments(LLMEventKind.ReasoningDelta, 1)]
    [Arguments(LLMEventKind.Completed, 1)]
    public async Task Empty_fragments_and_terminators_yield_nothing(
        LLMEventKind kind, int expectedCount, CancellationToken cancellationToken)
    {
        var events = await Drain(cancellationToken);

        _ = await Assert.That(events.Count(item => item.Kind == kind)).IsEqualTo(expectedCount);
    }

    [Test]
    public async Task Text_fragments_arrive_in_order(CancellationToken cancellationToken)
    {
        var events = await Drain(cancellationToken);
        var text = string.Concat(events.Where(item => item.Kind == LLMEventKind.TextDelta).Select(item => item.Text));

        _ = await Assert.That(text).IsEqualTo("hello world");
    }

    [Test]
    public async Task Tool_call_fragments_assemble_into_one_completed_call(CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(ToolCallStream));
        var events = new List<LLMEvent>();

        await foreach (var published in ChatCompletionsAdapter.Parse(stream, 4096, cancellationToken))
        {
            events.Add(published);
        }

        var completed = events[^1];
        var toolCall = events.Single(item => item.Kind == LLMEventKind.ToolCallDelta);

        _ = await Assert.That(toolCall.Text).IsEqualTo("""{"command": "echo hi"}""");
        _ = await Assert.That(completed.Kind).IsEqualTo(LLMEventKind.Completed);
        _ = await Assert.That(completed.FinishReason).IsEqualTo("tool_calls");
        _ = await Assert.That(completed.ToolCalls).HasSingleItem();
        _ = await Assert.That(completed.ToolCalls[0].Id).IsEqualTo("call_1");
        _ = await Assert.That(completed.ToolCalls[0].Name).IsEqualTo("exec_command");
        _ = await Assert.That(completed.ToolCalls[0].ArgumentsJson).IsEqualTo("""{"command": "echo hi"}""");
    }

    [Test]
    public async Task Structured_stream_errors_preserve_their_body(CancellationToken cancellationToken)
    {
        const string body = """{"error":{"type":"server_error","code":"bad","message":"boom"}}""";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes($"data: {body}\n\n"));
        ProviderResponseException? failure = null;

        try
        {
            await foreach (var published in ChatCompletionsAdapter.Parse(stream, 4096, cancellationToken))
            {
                _ = published;
            }
        }
        catch (ProviderResponseException caught)
        {
            failure = caught;
        }

        _ = await Assert.That(failure).IsNotNull();
        if (failure is null)
        {
            throw new InvalidOperationException("The provider failure was not raised.");
        }

        _ = await Assert.That(failure.ResponseBody).IsEqualTo(body);
    }

    [Test]
    public async Task Factories_never_produce_a_null_field(CancellationToken cancellationToken)
    {
        _ = await Assert.That(LLMEvent.TextDelta("x").ToolName).IsEmpty();
        _ = await Assert.That(LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429").ToolCallId).IsEmpty();
        _ = _ = await Assert.That(LLMEvent.Completed("stop", 1, 0, 2, "hi", []).Text).IsEmpty();
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    private static string WriteConfiguration(string directory, string content)
    {
        _ = Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "config.yaml");
        File.WriteAllText(path, content);
        return path;
    }

    private static async Task<List<LLMEvent>> Drain(CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(LiveShapedStream));
        var events = new List<LLMEvent>();

        await foreach (var published in ChatCompletionsAdapter.Parse(stream, 4096, cancellationToken))
        {
            events.Add(published);
        }

        return events;
    }
}

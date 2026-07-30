using System.Text;
using System.Text.Json;
using Parrot.Llm;
using Parrot.Llm.Wire;

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
    public async Task Encode_preserves_tool_and_generated_field_descriptions(CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = "vendor/model",
            Messages = [LLMMessage.User("hello")],
            Tools = [new LLMToolDefinition("write", "Creates or replaces a file.", Parrot.Tools.WriteToolInput.Descriptor)],
        };
        using var document = JsonDocument.Parse(ChatCompletionsAdapter.Encode(request));
        var function = document.RootElement.GetProperty("tools")[0].GetProperty("function");

        _ = await Assert.That(function.GetProperty("description").GetString())
            .IsEqualTo("Creates or replaces a file.");
        _ = await Assert.That(
            function.GetProperty("parameters").GetProperty("properties").GetProperty("path")
                .GetProperty("description").GetString())
            .IsEqualTo("Path of the file to create or replace.");
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
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
    [Arguments("Follow the repository instructions.", 3)]
    [Arguments("", 2)]
    public async Task Encode_prepends_nonempty_instructions_as_a_system_message(
        string instructions, int messageCount, CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = "vendor/model",
            Instructions = instructions,
            Messages = [LLMMessage.System("history guidance"), LLMMessage.User("hello")],
        };
        using var document = JsonDocument.Parse(ChatCompletionsAdapter.Encode(request));
        var root = document.RootElement;
        var messages = root.GetProperty("messages");

        _ = await Assert.That(root.TryGetProperty("instructions", out _)).IsFalse();
        _ = await Assert.That(messages.GetArrayLength()).IsEqualTo(messageCount);

        var historyIndex = 0;

        if (instructions.Length > 0)
        {
            _ = await Assert.That(messages[0].GetProperty("role").GetString()).IsEqualTo("system");
            _ = await Assert.That(messages[0].GetProperty("content").GetString()).IsEqualTo(instructions);
            historyIndex = 1;
        }

        _ = await Assert.That(messages[historyIndex].GetProperty("role").GetString()).IsEqualTo("system");
        _ = await Assert.That(messages[historyIndex].GetProperty("content").GetString())
            .IsEqualTo("history guidance");
        _ = await Assert.That(messages[historyIndex + 1].GetProperty("role").GetString()).IsEqualTo("user");
        _ = await Assert.That(messages[historyIndex + 1].GetProperty("content").GetString()).IsEqualTo("hello");
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
    public async Task Factories_never_produce_a_null_field(CancellationToken cancellationToken)
    {
        _ = await Assert.That(LLMEvent.TextDelta("x").ToolName).IsEmpty();
        _ = await Assert.That(LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429").ToolCallId).IsEmpty();
        _ = _ = await Assert.That(LLMEvent.Completed("stop", 1, 0, 2, "hi", []).Text).IsEmpty();
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
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

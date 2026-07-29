using System.Text;
using System.Text.Json;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ResponsesAdapterTests
{
    [Test]
    [Arguments("xhigh", true)]
    [Arguments("", false)]
    public async Task Encode_writes_or_omits_nested_reasoning(string effort, bool hasReasoning, CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = "gpt-5.6-sol",
            Messages = [LLMMessage.User("hello")],
            Reasoning = hasReasoning ? new ReasoningOptions(effort, "auto") : null,
        };
        using var document = JsonDocument.Parse(ResponsesAdapter.Encode(request));
        var root = document.RootElement;

        _ = await Assert.That(root.TryGetProperty("reasoning", out var reasoning)).IsEqualTo(hasReasoning);

        if (hasReasoning)
        {
            _ = await Assert.That(reasoning.GetProperty("effort").GetString()).IsEqualTo(effort);
            _ = await Assert.That(reasoning.GetProperty("summary").GetString()).IsEqualTo("auto");
        }

        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    [Arguments("Follow the repository instructions.", true)]
    [Arguments("", false)]
    public async Task Encode_writes_or_omits_top_level_instructions(
        string instructions, bool hasInstructions, CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = "gpt-5.6-sol",
            Instructions = instructions,
            Messages = [LLMMessage.System("history guidance"), LLMMessage.User("hello")],
        };
        using var document = JsonDocument.Parse(ResponsesAdapter.Encode(request));
        var root = document.RootElement;
        var input = root.GetProperty("input");

        _ = await Assert.That(root.TryGetProperty("instructions", out var encodedInstructions))
            .IsEqualTo(hasInstructions);

        if (hasInstructions)
        {
            _ = await Assert.That(encodedInstructions.GetString()).IsEqualTo(instructions);
        }

        _ = await Assert.That(input.GetArrayLength()).IsEqualTo(2);
        _ = await Assert.That(input[0].GetProperty("role").GetString()).IsEqualTo("developer");
        _ = await Assert.That(input[0].GetProperty("content")[0].GetProperty("text").GetString())
            .IsEqualTo("history guidance");
        _ = await Assert.That(input[1].GetProperty("role").GetString()).IsEqualTo("user");
        _ = await Assert.That(input[1].GetProperty("content")[0].GetProperty("text").GetString())
            .IsEqualTo("hello");
        _ = await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }

    [Test]
    public async Task Text_and_reasoning_deltas_fold_and_complete_with_usage(CancellationToken cancellationToken)
    {
        const string stream = """
            data: {"type":"response.reasoning_summary_text.delta","item_id":"r","summary_index":0,"delta":"pondering"}

            data: {"type":"response.reasoning_summary_text.done","item_id":"r","summary_index":0,"text":"pondering"}

            data: {"type":"response.reasoning_text.delta","item_id":"raw","content_index":0,"delta":"raw thought"}

            data: {"type":"response.output_text.annotation.added","item_id":"annotation","annotation_index":0,"delta":"citation"}

            data: {"type":"response.output_text.delta","delta":"hello"}

            data: {"type":"response.output_text.delta","delta":" world"}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":7,"input_tokens_details":{"cached_tokens":4},"output_tokens":3}}}

            """;

        var events = await Drain(stream, cancellationToken);
        var completed = events[^1];

        _ = await Assert.That(string.Concat(events.Where(e => e.Kind == LLMEventKind.TextDelta).Select(e => e.Text)))
            .IsEqualTo("hello world");
        var reasoning = events.Where(e => e.Kind == LLMEventKind.ReasoningDelta).ToArray();
        _ = await Assert.That(
            string.Join(" | ", reasoning.Select(e =>
                $"{e.ReasoningKind}:{e.ReasoningPartId}:{e.ReasoningCompleted}:{e.Text}")))
            .IsEqualTo(
                "Summary:r:0:False:pondering | Summary:r:0:True: | Raw:raw:0:False:raw thought | "
                + "Raw:annotation:0:False:citation");
        _ = await Assert.That(completed.FinishReason).IsEqualTo("stop");
        _ = await Assert.That(completed.AssistantText).IsEqualTo("hello world");
        _ = await Assert.That(completed.InputTokens).IsEqualTo(7);
        _ = await Assert.That(completed.CachedInputTokens).IsEqualTo(4);
        _ = await Assert.That(completed.OutputTokens).IsEqualTo(3);
    }

    [Test]
    public async Task Function_call_assembles_into_the_completed_event(CancellationToken cancellationToken)
    {
        const string stream = """
            data: {"type":"response.output_item.added","item":{"id":"i1","type":"function_call","call_id":"c1","name":"exec_command"}}

            data: {"type":"response.function_call_arguments.delta","item_id":"i1","call_id":"c1","delta":"{\"command\": "}

            data: {"type":"response.function_call_arguments.delta","item_id":"i1","call_id":"c1","delta":"\"echo hi\"}"}

            data: {"type":"response.output_item.done","item":{"id":"i1","type":"function_call","call_id":"c1","name":"exec_command","arguments":"{\"command\": \"echo hi\"}"}}

            data: {"type":"response.completed","response":{"status":"completed"}}

            """;

        var events = await Drain(stream, cancellationToken);
        var completed = events[^1];

        var toolCall = events.Single(e => e.Kind == LLMEventKind.ToolCallDelta);
        _ = await Assert.That(toolCall.Text).IsEqualTo("""{"command": "echo hi"}""");
        _ = await Assert.That(completed.FinishReason).IsEqualTo("tool_calls");
        _ = await Assert.That(completed.ToolCalls).HasSingleItem();
        _ = await Assert.That(completed.ToolCalls[0].Id).IsEqualTo("c1");
        _ = await Assert.That(completed.ToolCalls[0].Name).IsEqualTo("exec_command");
        _ = await Assert.That(completed.ToolCalls[0].ArgumentsJson).IsEqualTo("""{"command": "echo hi"}""");
    }

    [Test]
    public async Task An_incomplete_response_reports_the_budget_reason(CancellationToken cancellationToken)
    {
        const string stream = """
            data: {"type":"response.incomplete","response":{"incomplete_details":{"reason":"max_output_tokens"}}}

            """;

        var events = await Drain(stream, cancellationToken);

        _ = await Assert.That(events[^1].FinishReason).IsEqualTo("length");
    }

    [Test]
    [Arguments("""data: {"type":"response.failed","response":{"error":{"type":"server_error","message":"boom"}}}""")]
    [Arguments("""data: {"type":"error","code":"bad","message":"nope"}""")]
    public async Task A_structured_stream_error_is_raised(string line, CancellationToken cancellationToken) =>
        _ = await Assert.That(async () => await Drain(line + "\n\n", cancellationToken))
            .Throws<ProviderResponseException>();

    [Test]
    public async Task A_stream_without_a_terminal_event_is_an_error(CancellationToken cancellationToken)
    {
        const string truncated = """
            data: {"type":"response.output_text.delta","delta":"hi"}

            """;

        _ = await Assert.That(async () => await Drain(truncated, cancellationToken)).Throws<WireProtocolException>();
    }

    private static async Task<List<LLMEvent>> Drain(string stream, CancellationToken cancellationToken)
    {
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(stream));
        var events = new List<LLMEvent>();

        await foreach (var published in ResponsesAdapter.Parse(source, 4096, cancellationToken))
        {
            events.Add(published);
        }

        return events;
    }
}

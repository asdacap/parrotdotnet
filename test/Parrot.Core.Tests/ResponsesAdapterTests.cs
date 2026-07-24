using System.Text;
using Parrot.Llm;
using Parrot.Llm.Wire;

namespace Parrot.Core.Tests;

internal sealed class ResponsesAdapterTests
{
    [Test]
    public async Task Text_and_reasoning_deltas_fold_and_complete_with_usage(CancellationToken cancellationToken)
    {
        const string stream = """
            data: {"type":"response.reasoning_summary_text.delta","item_id":"r","summary_index":0,"delta":"pondering"}

            data: {"type":"response.output_text.delta","delta":"hello"}

            data: {"type":"response.output_text.delta","delta":" world"}

            data: {"type":"response.completed","response":{"status":"completed","usage":{"input_tokens":7,"output_tokens":3}}}

            """;

        var events = await Drain(stream, cancellationToken);
        var completed = events[^1];

        _ = await Assert.That(string.Concat(events.Where(e => e.Kind == LLMEventKind.TextDelta).Select(e => e.Text)))
            .IsEqualTo("hello world");
        _ = await Assert.That(events.Count(e => e.Kind == LLMEventKind.ReasoningDelta)).IsEqualTo(1);
        _ = await Assert.That(completed.FinishReason).IsEqualTo("stop");
        _ = await Assert.That(completed.AssistantText).IsEqualTo("hello world");
        _ = await Assert.That(completed.InputTokens).IsEqualTo(7);
        _ = await Assert.That(completed.OutputTokens).IsEqualTo(3);
    }

    [Test]
    public async Task Function_call_assembles_into_the_completed_event(CancellationToken cancellationToken)
    {
        const string stream = """
            data: {"type":"response.output_item.added","item":{"id":"i1","type":"function_call","call_id":"c1","name":"exec_command"}}

            data: {"type":"response.function_call_arguments.delta","item_id":"i1","call_id":"c1","delta":"{\"command\": \"echo hi\"}"}

            data: {"type":"response.output_item.done","item":{"id":"i1","type":"function_call","call_id":"c1","name":"exec_command","arguments":"{\"command\": \"echo hi\"}"}}

            data: {"type":"response.completed","response":{"status":"completed"}}

            """;

        var events = await Drain(stream, cancellationToken);
        var completed = events[^1];

        _ = await Assert.That(events.Count(e => e.Kind == LLMEventKind.ToolCallDelta)).IsEqualTo(1);
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

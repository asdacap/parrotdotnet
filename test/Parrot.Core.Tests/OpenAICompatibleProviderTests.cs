using System.Text;
using Parrot.Llm;

namespace Parrot.Core.Tests;

public class OpenAICompatibleProviderTests
{
    // Captured verbatim from the live OpenCode Go endpoint: content and
    // reasoning_content arrive as separate fields on the same delta.
    private const string LiveShapedStream = """
        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"role":"assistant","content":"","reasoning_content":null}}],"usage":null}

        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"content":"","reasoning_content":"think"}}],"usage":null}

        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"content":"hello ","reasoning_content":null}}],"usage":null}

        data: {"choices":[{"index":0,"finish_reason":null,"delta":{"content":"world","reasoning_content":null}}],"usage":null}

        data: {"choices":[{"index":0,"finish_reason":"stop","delta":{}}],"usage":{"prompt_tokens":11,"completion_tokens":3}}

        data: [DONE]

        """;

    [Test]
    public async Task Stream_is_split_into_deltas_and_a_final_result(CancellationToken cancellationToken)
    {
        var sink = new RecordingEventSink();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(LiveShapedStream));

        var result = await OpenAICompatibleProvider.Consume(stream, sink, cancellationToken);

        await Assert.That(result.Text).IsEqualTo("hello world");
        await Assert.That(result.Reasoning).IsEqualTo("think");
        await Assert.That(result.FinishReason).IsEqualTo("stop");
        await Assert.That(result.Usage.InputTokens).IsEqualTo(11);
        await Assert.That(result.Usage.OutputTokens).IsEqualTo(3);
    }

    [Test]
    [Arguments(LLMEventKind.TextDelta, 2)]
    [Arguments(LLMEventKind.ReasoningDelta, 1)]
    public async Task Empty_fragments_and_terminators_publish_nothing(
        LLMEventKind kind, int expectedCount, CancellationToken cancellationToken)
    {
        var sink = new RecordingEventSink();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(LiveShapedStream));

        _ = await OpenAICompatibleProvider.Consume(stream, sink, cancellationToken);

        await Assert.That(sink.Events.Count(item => item.Kind == kind)).IsEqualTo(expectedCount);
    }

    [Test]
    public async Task Factories_never_produce_a_null_field(CancellationToken cancellationToken)
    {
        await Assert.That(LLMEvent.TextDelta("x").ToolName).IsEmpty();
        await Assert.That(LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429").ToolCallId).IsEmpty();
        await Assert.That(LLMEvent.ToolCallDelta("id", "grep", "{}").Attempt).IsEqualTo(0);
        await Assert.That(cancellationToken.IsCancellationRequested).IsFalse();
    }
}

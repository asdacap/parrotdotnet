using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Core.Tests;

internal sealed class EventPayloadTests
{
    // Every event carries both: the one rendered line BasicCli prints, and the
    // typed payload EnhancedCli reads. Neither may compensate for the other.
    [Test]
    [Arguments(LLMEventKind.TextDelta, Event.PayloadOneofCase.TextChunk, EventKind.Text)]
    [Arguments(LLMEventKind.ReasoningDelta, Event.PayloadOneofCase.ReasoningChunk, EventKind.Reasoning)]
    [Arguments(LLMEventKind.ToolCallDelta, Event.PayloadOneofCase.ToolCallChunk, EventKind.ToolCall)]
    [Arguments(LLMEventKind.Retry, Event.PayloadOneofCase.RetryNotice, EventKind.Retry)]
    public async Task Each_llm_event_maps_to_a_kind_and_a_payload(
        LLMEventKind source,
        Event.PayloadOneofCase expectedPayload,
        EventKind expectedKind,
        CancellationToken cancellationToken)
    {
        var events = new EventBroker();
        var session = new AgentSession("session", new UnusedProvider(), events);

        var llmEvent = source switch
        {
            LLMEventKind.TextDelta => LLMEvent.TextDelta("fragment"),
            LLMEventKind.ReasoningDelta => LLMEvent.ReasoningDelta("thought"),
            LLMEventKind.ToolCallDelta => LLMEvent.ToolCallDelta("call", "grep", "{}"),
            _ => LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429"),
        };

        await session.Publish(llmEvent, cancellationToken);
        events.Complete();

        var published = await FirstOf(events, cancellationToken);

        await Assert.That(published.Kind).IsEqualTo(expectedKind);
        await Assert.That(published.PayloadCase).IsEqualTo(expectedPayload);
        await Assert.That(published.Text).IsNotEmpty();
        await Assert.That(published.SessionId).IsEqualTo("session");
    }

    private static async Task<Event> FirstOf(EventBroker events, CancellationToken cancellationToken)
    {
        await foreach (var published in events.Subscribe(cancellationToken))
        {
            return published;
        }

        throw new InvalidOperationException("the broker published nothing");
    }
}

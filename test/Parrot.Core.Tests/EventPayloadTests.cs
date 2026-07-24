using Parrot.Agent;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Core.Tests;

internal sealed class EventPayloadTests
{
    // The payload is the only discriminator, so this is what pins the mapping.
    [Test]
    [Arguments(LLMEventKind.TextDelta, Event.PayloadOneofCase.TextChunk)]
    [Arguments(LLMEventKind.ReasoningDelta, Event.PayloadOneofCase.ReasoningChunk)]
    [Arguments(LLMEventKind.ToolCallDelta, Event.PayloadOneofCase.ToolCallChunk)]
    [Arguments(LLMEventKind.Retry, Event.PayloadOneofCase.RetryNotice)]
    public async Task Each_llm_event_maps_to_a_payload(
        LLMEventKind source,
        Event.PayloadOneofCase expectedPayload)
    {
        using var events = new EventBroker();
        var session = new AgentSession("session", new UnusedProvider(), events);

        var llmEvent = source switch
        {
            LLMEventKind.TextDelta => LLMEvent.TextDelta("fragment"),
            LLMEventKind.ReasoningDelta => LLMEvent.ReasoningDelta("thought"),
            LLMEventKind.ToolCallDelta => LLMEvent.ToolCallDelta("call", "grep", "{}"),
            _ => LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429"),
        };

        var published = session.Translate(llmEvent);

        await Assert.That(published.PayloadCase).IsEqualTo(expectedPayload);
        await Assert.That(published.AgentSessionId).IsEqualTo("session");
        await Assert.That(published.Id).IsNotEmpty();
    }
}

using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

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

        // A real repository over an in-memory database, not a null: the code
        // under test should take the same path production does.
        using var database = SessionDatabase.Open(":memory:");
        var session = new AgentSession(
            "session",
            new UnusedProvider(),
            events,
            new EventRepository(database),
            [],
            new SystemContextBuilder(".", "2026-07-24"),
            new Compactor(120_000),
            depth: 0,
            CancellationToken.None);

        var llmEvent = source switch
        {
            LLMEventKind.TextDelta => LLMEvent.TextDelta("fragment"),
            LLMEventKind.ReasoningDelta => LLMEvent.ReasoningDelta("thought"),
            LLMEventKind.ToolCallDelta => LLMEvent.ToolCallDelta("call", "grep", "{}"),
            _ => LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429"),
        };

        var published = session.Translate(llmEvent);

        _ = await Assert.That(published.PayloadCase).IsEqualTo(expectedPayload);
        _ = await Assert.That(published.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(published.Id).IsNotEmpty();
    }
}

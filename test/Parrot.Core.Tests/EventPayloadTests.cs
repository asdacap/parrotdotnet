using Google.Protobuf;
using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class EventPayloadTests
{
    [Test]
    public async Task Agent_statistics_roundtrip_as_a_protobuf_payload()
    {
        var source = new Event
        {
            Id = "statistics-event",
            AgentSessionId = "session",
            AgentStatisticsUpdated = new AgentStatisticsUpdatedEvent
            {
                InputTokens = 4_000_000_000,
                CachedInputTokens = 3_000_000_000,
                OutputTokens = 2_000_000_000,
                ContextSize = 100_000,
                ContextLimit = 500_000,
                InputCost = 12.5,
                OutputCost = 7.25,
            },
        };

        var roundtripped = Event.Parser.ParseFrom(source.ToByteArray());

        _ = await Assert.That(roundtripped.Id).IsEqualTo("statistics-event");
        _ = await Assert.That(roundtripped.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.AgentStatisticsUpdated);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.InputTokens).IsEqualTo(4_000_000_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.CachedInputTokens).IsEqualTo(3_000_000_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.OutputTokens).IsEqualTo(2_000_000_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.ContextSize).IsEqualTo(100_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.ContextLimit).IsEqualTo(500_000);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.InputCost).IsEqualTo(12.5);
        _ = await Assert.That(roundtripped.AgentStatisticsUpdated.OutputCost).IsEqualTo(7.25);
    }

    [Test]
    public async Task Status_injected_roundtrips_as_a_protobuf_payload()
    {
        var source = new Event
        {
            Id = "status-event",
            AgentSessionId = "session",
            StatusInjected = new StatusInjected(),
        };

        var roundtripped = Event.Parser.ParseFrom(source.ToByteArray());

        _ = await Assert.That(roundtripped.Id).IsEqualTo("status-event");
        _ = await Assert.That(roundtripped.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(roundtripped.PayloadCase).IsEqualTo(Event.PayloadOneofCase.StatusInjected);
    }

    [Test]
    public async Task Tool_finished_result_preserves_absent_empty_and_nonempty_presence()
    {
        var absent = new Event { ToolFinished = new ToolFinished() };
        var empty = new Event { ToolFinished = new ToolFinished { Result = string.Empty } };
        var nonempty = new Event { ToolFinished = new ToolFinished { Result = "done" } };

        var roundtrippedAbsent = Event.Parser.ParseFrom(absent.ToByteArray());
        var roundtrippedEmpty = Event.Parser.ParseFrom(empty.ToByteArray());
        var roundtrippedNonempty = Event.Parser.ParseFrom(nonempty.ToByteArray());

        _ = await Assert.That(roundtrippedAbsent.ToolFinished.HasResult).IsFalse();
        _ = await Assert.That(roundtrippedEmpty.ToolFinished.HasResult).IsTrue();
        _ = await Assert.That(roundtrippedEmpty.ToolFinished.Result).IsEqualTo(string.Empty);
        _ = await Assert.That(roundtrippedNonempty.ToolFinished.HasResult).IsTrue();
        _ = await Assert.That(roundtrippedNonempty.ToolFinished.Result).IsEqualTo("done");
    }

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
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var session = new AgentSession(
            AgentIdentity.Main("session", string.Empty),
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            new EventRepository(database),
            [],
            TestModels.PromptProvider(".", "."),
            new TodoCollection("session", new EventRepository(database), events),
            new Compactor(120_000),
            profile: null,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            status: null,
            CancellationToken.None);

        var llmEvent = source switch
        {
            LLMEventKind.TextDelta => LLMEvent.TextDelta("fragment"),
            LLMEventKind.ReasoningDelta => LLMEvent.ReasoningDelta(
                "thought", LLMReasoningKind.Summary, "reasoning-1", completed: true),
            LLMEventKind.ToolCallDelta => LLMEvent.ToolCallDelta("call", "grep", "{}"),
            _ => LLMEvent.Retry(2, TimeSpan.FromSeconds(1), "429"),
        };

        var published = session.Translate(llmEvent);

        _ = await Assert.That(published.PayloadCase).IsEqualTo(expectedPayload);
        _ = await Assert.That(published.AgentSessionId).IsEqualTo("session");
        _ = await Assert.That(published.Id).IsNotEmpty();

        if (source == LLMEventKind.ReasoningDelta)
        {
            _ = await Assert.That(published.ReasoningChunk.Kind).IsEqualTo(ReasoningKind.Summary);
            _ = await Assert.That(published.ReasoningChunk.PartId).IsEqualTo("reasoning-1");
            _ = await Assert.That(published.ReasoningChunk.Completed).IsTrue();
        }
    }
}

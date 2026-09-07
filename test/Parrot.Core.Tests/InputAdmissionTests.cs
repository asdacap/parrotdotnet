using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Core.Tests;

// Admission and promotion against a real database, because what is being
// tested is the transaction: the input settles, the conversation gains the
// message and the event becomes durable, or none of the three does.
internal sealed class InputAdmissionTests : IDisposable
{
    private const string Session = "agent";

    private readonly SessionDatabase _database = SessionDatabase.Open(":memory:");
    private readonly EventRepository _repository;

    public InputAdmissionTests() => _repository = new EventRepository(_database);

    public void Dispose() => _database.Dispose();

    [Test]
    [Arguments(Delivery.Steer)]
    [Arguments(Delivery.Queue)]
    public async Task An_admitted_prompt_waits_for_the_boundary_its_delivery_names(Delivery delivery)
    {
        var admitted = _repository.Admit(Session, "msg-1", "hello", delivery, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        _ = await Assert.That(admitted.Created).IsTrue();
        _ = await Assert.That(_repository.HasPendingInputs(Session)).IsTrue();

        // Not part of the conversation yet: admitting is not promoting.
        _ = await Assert.That(_repository.Messages(Session)).IsEmpty();

        // The other delivery's boundary leaves it alone.
        _ = await Assert.That(Promote(Other(delivery))).IsEqualTo(0);
        _ = await Assert.That(_repository.HasPendingInputs(Session)).IsTrue();

        _ = await Assert.That(Promote(delivery)).IsEqualTo(1);
        _ = await Assert.That(_repository.HasPendingInputs(Session)).IsFalse();
        _ = await Assert.That(_repository.Messages(Session)).Contains("user: hello");

        // Promoted once. A second boundary does not hand it over again.
        _ = await Assert.That(Promote(delivery)).IsEqualTo(0);
    }

    [Test]
    public async Task A_message_id_names_one_prompt_and_re_sending_it_admits_nothing_new()
    {
        var first = _repository.Admit(Session, "msg-1", "hello", Delivery.Steer, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });
        var again = _repository.Admit(Session, "msg-1", "hello", Delivery.Steer, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        _ = await Assert.That(again.Created).IsFalse();
        _ = await Assert.That(again.Input.Id).IsEqualTo(first.Input.Id);
        _ = await Assert.That(Promote(Delivery.Steer)).IsEqualTo(1);

        _ = await Assert.That(() => _repository.Admit(
            Session, "msg-1", "different", Delivery.Steer, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session }))
            .Throws<InputConflictException>();
    }

    [Test]
    public async Task Structured_input_promotion_preserves_parts_and_retries_idempotently()
    {
        var parts = new[]
        {
            ConversationPart.TextPart("before"),
            ConversationPart.ImageArtifact("artifact-1", "image/png", "pixel.png"),
            ConversationPart.TextPart("after"),
        };

        var admitted = _repository.Admit(
            Session,
            "msg-structured",
            parts,
            Delivery.Steer,
            static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });
        var retried = _repository.Admit(
            Session,
            "msg-structured",
            parts,
            Delivery.Steer,
            static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        _ = await Assert.That(admitted.Created).IsTrue();
        _ = await Assert.That(retried.Created).IsFalse();
        _ = await Assert.That(retried.Input.Id).IsEqualTo(admitted.Input.Id);
        _ = await Assert.That(Promote(Delivery.Steer)).IsEqualTo(1);

        var conversation = _repository.Conversation(Session).Single();
        _ = await Assert.That(conversation.Origin).IsEqualTo(ConversationOrigin.UserInput);
        _ = await Assert.That(string.Join(',', conversation.Parts.Select(part => part.Kind)))
            .IsEqualTo("Text,ImageArtifact,Text");
        _ = await Assert.That(conversation.Parts[0].Text).IsEqualTo("before");
        _ = await Assert.That(conversation.Parts[1].ArtifactId).IsEqualTo("artifact-1");
        _ = await Assert.That(conversation.Parts[1].MediaType).IsEqualTo("image/png");
        _ = await Assert.That(conversation.Parts[1].DisplayName).IsEqualTo("pixel.png");
        _ = await Assert.That(conversation.Parts[2].Text).IsEqualTo("after");
    }

    [Test]
    public async Task Conditional_steer_yields_to_pending_input_and_retries_idempotently()
    {
        _ = _repository.Admit(
            Session,
            "normal",
            "normal prompt",
            Delivery.Queue,
            static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        var blocked = _repository.AdmitSteerIfIdle(
            Session,
            "delivery",
            "notification",
            static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        _ = await Assert.That(blocked).IsNull();
        _ = Promote(Delivery.Queue);

        var admitted = _repository.AdmitSteerIfIdle(
            Session,
            "delivery",
            "notification",
            static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });
        var retried = _repository.AdmitSteerIfIdle(
            Session,
            "delivery",
            "notification",
            static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        if (admitted is null || retried is null)
        {
            throw new InvalidOperationException("conditional admission unexpectedly failed");
        }

        _ = await Assert.That(admitted.Created).IsTrue();
        _ = await Assert.That(retried.Created).IsFalse();
        _ = await Assert.That(retried.Input.Id).IsEqualTo(admitted.Input.Id);
    }

    [Test]
    public async Task Conditional_steer_rejects_a_contradictory_retry()
    {
        _ = _repository.AdmitSteerIfIdle(
            Session,
            "delivery",
            "notification",
            static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        _ = await Assert.That(() => _repository.AdmitSteerIfIdle(
            Session,
            "delivery",
            "different",
            static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session }))
            .Throws<InputConflictException>();
    }

    [Test]
    public async Task Steers_promote_together_and_queued_prompts_one_at_a_time()
    {
        _ = _repository.Admit(Session, "msg-1", "first steer", Delivery.Steer, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });
        _ = _repository.Admit(Session, "msg-2", "second steer", Delivery.Steer, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });
        _ = _repository.Admit(Session, "msg-3", "first queued", Delivery.Queue, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });
        _ = _repository.Admit(Session, "msg-4", "second queued", Delivery.Queue, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        var steers = _repository.PromoteSteers(Session, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });

        _ = await Assert.That(string.Join(" | ", steers.Select(promoted => promoted.Input.Content)))
            .IsEqualTo("first steer | second steer");

        _ = await Assert.That(Queued()).IsEqualTo("first queued");
        _ = await Assert.That(Queued()).IsEqualTo("second queued");
        _ = await Assert.That(_repository.PromoteNextQueue(Session, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session })).IsEmpty();
    }

    [Test]
    [Arguments(Delivery.Steer)]
    [Arguments(Delivery.Queue)]
    public async Task Cancellation_only_removes_the_owned_pending_input_and_records_one_event(Delivery delivery)
    {
        var admitted = _repository.Admit(Session, "cancel", "canceled prompt", delivery, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });
        var unrelated = _repository.Admit(Session, "unrelated", "unrelated prompt", delivery, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session });
        var otherSession = _repository.Admit("other-agent", "other", "other prompt", delivery, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = "other-agent" });
        var canceledEvent = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = Session,
            InputCanceled = new InputCanceled { InputId = admitted.Input.Id },
        };

        _ = await Assert.That(_repository.CancelPendingInput("other-agent", admitted.Input.Id, static () => throw new InvalidOperationException("wrong session"))).IsNull();
        _ = await Assert.That(_repository.CancelPendingInput(Session, otherSession.Input.Id, static () => throw new InvalidOperationException("wrong owner"))).IsNull();
        _ = await Assert.That(_repository.CancelPendingInput(Session, "missing", static () => throw new InvalidOperationException("missing input"))).IsNull();
        _ = await Assert.That(_repository.CancelPendingInput(Session, admitted.Input.Id, () => canceledEvent)).IsEqualTo(canceledEvent);
        _ = await Assert.That(_repository.CancelPendingInput(Session, admitted.Input.Id, static () => throw new InvalidOperationException("already canceled"))).IsNull();

        var recorded = _repository.Replay().Single(published => published.PayloadCase == Event.PayloadOneofCase.InputCanceled);
        _ = await Assert.That(recorded).IsEqualTo(canceledEvent);
        _ = await Assert.That(_repository.InputsForNextPromotion(Session).Single().Id).IsEqualTo(unrelated.Input.Id);
        _ = await Assert.That(_repository.InputsForNextPromotion("other-agent").Single().Id).IsEqualTo(otherSession.Input.Id);
        _ = await Assert.That(Promote(delivery)).IsEqualTo(1);
        _ = await Assert.That(_repository.Messages(Session).Single()).IsEqualTo("user: unrelated prompt");
        _ = await Assert.That(_repository.HasPendingInputs(Session)).IsFalse();
        _ = await Assert.That(_repository.CancelPendingInput(Session, unrelated.Input.Id, static () => throw new InvalidOperationException("already promoted"))).IsNull();
        _ = await Assert.That(_repository.Replay().Count(published => published.PayloadCase == Event.PayloadOneofCase.InputCanceled)).IsEqualTo(1);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Failed_cancellation_rolls_back_the_input_and_event(bool failComposition)
    {
        var admittedEvent = new Event { Id = Identifier.EventId(), AgentSessionId = Session };
        var admitted = _repository.Admit(Session, "cancel", "pending prompt", Delivery.Queue, _ => admittedEvent);
        var canceledEvent = new Event
        {
            Id = admittedEvent.Id,
            AgentSessionId = Session,
            InputCanceled = new InputCanceled { InputId = admitted.Input.Id },
        };

        if (failComposition)
        {
            _ = await Assert.That(() => _repository.CancelPendingInput(Session, admitted.Input.Id, static () => throw new InvalidOperationException("composition failed")))
                .Throws<InvalidOperationException>();
        }
        else
        {
            _ = await Assert.That(() => _repository.CancelPendingInput(Session, admitted.Input.Id, () => canceledEvent))
                .Throws<Microsoft.Data.Sqlite.SqliteException>();
        }

        _ = await Assert.That(_repository.InputsForNextPromotion(Session).Single().Id).IsEqualTo(admitted.Input.Id);
        _ = await Assert.That(_repository.Replay().Single()).IsEqualTo(admittedEvent);
        canceledEvent.Id = Identifier.EventId();
        _ = await Assert.That(_repository.CancelPendingInput(Session, admitted.Input.Id, () => canceledEvent)).IsEqualTo(canceledEvent);
        _ = await Assert.That(_repository.HasPendingInputs(Session)).IsFalse();
        _ = await Assert.That(_repository.Replay()[^1]).IsEqualTo(canceledEvent);
        _ = await Assert.That(Promote(Delivery.Queue)).IsEqualTo(0);
        _ = await Assert.That(_repository.Messages(Session)).IsEmpty();
    }

    private static Delivery Other(Delivery delivery) =>
        delivery == Delivery.Steer ? Delivery.Queue : Delivery.Steer;

    private int Promote(Delivery delivery) =>
        delivery == Delivery.Steer
            ? _repository.PromoteSteers(Session, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session }).Count
            : _repository.PromoteNextQueue(Session, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session }).Count;

    private string Queued() =>
        _repository.PromoteNextQueue(Session, static _ => new Event { Id = Identifier.EventId(), AgentSessionId = Session }).Single().Input.Content;
}

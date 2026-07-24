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
        var admitted = _repository.Admit(Session, "msg-1", "hello", delivery, Announce);

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
        var first = _repository.Admit(Session, "msg-1", "hello", Delivery.Steer, Announce);
        var again = _repository.Admit(Session, "msg-1", "hello", Delivery.Steer, Announce);

        _ = await Assert.That(again.Created).IsFalse();
        _ = await Assert.That(again.Input.Id).IsEqualTo(first.Input.Id);
        _ = await Assert.That(Promote(Delivery.Steer)).IsEqualTo(1);

        _ = await Assert.That(() => _repository.Admit(Session, "msg-1", "different", Delivery.Steer, Announce))
            .Throws<InputConflictException>();
    }

    [Test]
    public async Task Steers_promote_together_and_queued_prompts_one_at_a_time()
    {
        _ = _repository.Admit(Session, "msg-1", "first steer", Delivery.Steer, Announce);
        _ = _repository.Admit(Session, "msg-2", "second steer", Delivery.Steer, Announce);
        _ = _repository.Admit(Session, "msg-3", "first queued", Delivery.Queue, Announce);
        _ = _repository.Admit(Session, "msg-4", "second queued", Delivery.Queue, Announce);

        var steers = _repository.PromoteSteers(Session, Announce);

        _ = await Assert.That(string.Join(" | ", steers.Select(promoted => promoted.Input.Content)))
            .IsEqualTo("first steer | second steer");

        _ = await Assert.That(Queued()).IsEqualTo("first queued");
        _ = await Assert.That(Queued()).IsEqualTo("second queued");
        _ = await Assert.That(_repository.PromoteNextQueue(Session, Announce)).IsEmpty();
    }

    private static Delivery Other(Delivery delivery) =>
        delivery == Delivery.Steer ? Delivery.Queue : Delivery.Steer;

    private static Event Announce(AdmittedInput input) =>
        new() { Id = Identifier.EventId(), AgentSessionId = Session };

    private int Promote(Delivery delivery) =>
        delivery == Delivery.Steer
            ? _repository.PromoteSteers(Session, Announce).Count
            : _repository.PromoteNextQueue(Session, Announce).Count;

    private string Queued() =>
        _repository.PromoteNextQueue(Session, Announce).Single().Input.Content;
}

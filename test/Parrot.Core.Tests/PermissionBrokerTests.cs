using Parrot.Agent;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Permissions;
using Parrot.Security;
using Parrot.Store;

namespace Parrot.Core.Tests;

internal sealed class PermissionBrokerTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "parrot-permission-broker-tests",
        Guid.NewGuid().ToString("n"));

    public PermissionBrokerTests() => Directory.CreateDirectory(_root);

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Test]
    public async Task Grant_adds_every_target_only_to_requesting_agent_and_publishes_event(
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            TimeSpan.FromSeconds(30),
            TimeProvider.System);
        var requesting = Session("requesting", database, events);
        var other = Session("other", database, events);
        var first = Target("first");
        var second = Target("second");
        var request = broker.Request(requesting, "update generated files", [first, second], cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        _ = await Assert.That(pending.AgentSessionId).IsEqualTo("requesting");
        _ = await Assert.That(pending.Reason).IsEqualTo("update generated files");
        _ = await Assert.That(pending.Targets).Count().IsEqualTo(2);
        _ = await Assert.That(string.Join(",", pending.Choices.Select(choice => choice.Value)))
            .IsEqualTo("grant,reject,reject with reason");
        _ = await Assert.That(new EventRepository(database).Replay().Single().PermissionPending.Id)
            .IsEqualTo(pending.Id);

        broker.Reply(pending.Id, "grant", string.Empty);

        _ = await Assert.That((await request).Decision).IsEqualTo(PermissionDecision.Grant);
        _ = await Assert.That(requesting.WriteGrants.Capture().Targets).Count().IsEqualTo(2);
        _ = await Assert.That(other.WriteGrants.Capture().Targets).IsEmpty();
        _ = await Assert.That(broker.Pending()).IsEmpty();
    }

    [Test]
    public async Task Rejection_reason_is_returned_and_invalid_reply_leaves_request_pending(
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            TimeSpan.FromSeconds(30),
            TimeProvider.System);
        var request = broker.Request(
            Session("requesting", database, events),
            "modify dependency",
            [Target("dependency")],
            cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        _ = await Assert.That(() => broker.Reply(pending.Id, "grant", "not now"))
            .Throws<PermissionException>();
        _ = await Assert.That(broker.Pending()).Count().IsEqualTo(1);

        broker.Reply(pending.Id, "reject with reason", "use the checked-in version");

        var reply = await request;
        _ = await Assert.That(reply.Decision).IsEqualTo(PermissionDecision.Reject);
        _ = await Assert.That(reply.Reason).IsEqualTo("use the checked-in version");
        _ = await Assert.That(() => broker.Reply(pending.Id, "reject", string.Empty))
            .Throws<PermissionNotFoundException>();
    }

    [Test]
    public async Task Noninteractive_requests_reject_without_creating_pending(
        CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: false,
            TimeSpan.FromSeconds(30),
            TimeProvider.System);

        var reply = await broker.Request(
            Session("requesting", database, events),
            "modify dependency",
            [Target("dependency")],
            cancellationToken);

        _ = await Assert.That(reply.Decision).IsEqualTo(PermissionDecision.Reject);
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(new EventRepository(database).Replay()).IsEmpty();
    }

    [Test]
    public async Task Timeout_returns_user_away_removes_pending_and_late_reply_is_not_found(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        var time = new ControlledTimeProvider();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            TimeSpan.FromMinutes(20),
            time);
        var request = broker.Request(
            Session("requesting", database, events),
            "modify dependency",
            [Target("dependency")],
            cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);
        await time.WaitForTimer(cancellationToken);

        time.Advance(TimeSpan.FromMinutes(20));

        _ = await Assert.That((await request).Kind).IsEqualTo(PermissionReplyKind.UserAway);
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(() => broker.Reply(pending.Id, "grant", string.Empty))
            .Throws<PermissionNotFoundException>();
    }

    [Test]
    public async Task Infinite_timeout_stays_pending_until_replied(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        var time = new ControlledTimeProvider();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            Timeout.InfiniteTimeSpan,
            time);
        var request = broker.Request(
            Session("requesting", database, events),
            "modify dependency",
            [Target("dependency")],
            cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);

        time.Advance(TimeSpan.FromDays(1));
        _ = await Assert.That(request.IsCompleted).IsFalse();
        broker.Reply(pending.Id, "reject", string.Empty);

        _ = await Assert.That((await request).Decision).IsEqualTo(PermissionDecision.Reject);
    }

    [Test]
    public async Task Reply_before_timeout_wins_and_timeout_does_not_replace_it(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        var time = new ControlledTimeProvider();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            TimeSpan.FromMinutes(20),
            time);
        var request = broker.Request(
            Session("requesting", database, events),
            "modify dependency",
            [Target("dependency")],
            cancellationToken);
        var pending = await WaitForPending(broker, cancellationToken);
        await time.WaitForTimer(cancellationToken);

        broker.Reply(pending.Id, "reject", string.Empty);
        time.Advance(TimeSpan.FromMinutes(20));

        _ = await Assert.That((await request).Decision).IsEqualTo(PermissionDecision.Reject);
    }

    [Test]
    public async Task Reply_before_cancellation_wins_and_cancellation_does_not_replace_it(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            Timeout.InfiniteTimeSpan,
            TimeProvider.System);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var request = broker.Request(
            Session("requesting", database, events),
            "modify dependency",
            [Target("dependency")],
            stopping.Token);
        var pending = await WaitForPending(broker, cancellationToken);

        broker.Reply(pending.Id, "reject", string.Empty);
        await stopping.CancelAsync();

        _ = await Assert.That((await request).Decision).IsEqualTo(PermissionDecision.Reject);
    }

    [Test]
    public async Task Cancellation_removes_pending_and_rejects_late_reply(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        using var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            Timeout.InfiniteTimeSpan,
            TimeProvider.System);
        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var request = broker.Request(
            Session("requesting", database, events),
            "modify dependency",
            [Target("dependency")],
            stopping.Token);
        var pending = await WaitForPending(broker, cancellationToken);

        await stopping.CancelAsync();

        _ = await Assert.That(request).Throws<OperationCanceledException>();
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(() => broker.Reply(pending.Id, "grant", string.Empty))
            .Throws<PermissionNotFoundException>();
    }

    [Test]
    public async Task Disposal_rejects_pending_and_future_requests(CancellationToken cancellationToken)
    {
        using var database = SessionDatabase.Open(":memory:");
        using var events = new EventBroker();
        var broker = new PermissionBroker(
            events,
            new EventRepository(database),
            interactive: true,
            Timeout.InfiniteTimeSpan,
            TimeProvider.System);
        var session = Session("requesting", database, events);
        var target = Target("dependency");
        var request = broker.Request(
            session,
            "modify dependency",
            [target],
            cancellationToken);
        _ = await WaitForPending(broker, cancellationToken);

        broker.Dispose();
        broker.Dispose();

        _ = await Assert.That(request).Throws<PermissionException>();
        _ = await Assert.That(broker.Pending()).IsEmpty();
        _ = await Assert.That(async () => await broker.Request(
            session,
            "modify dependency",
            [target],
            cancellationToken)).Throws<ObjectDisposedException>();
    }

    private static AgentSession Session(string id, SessionDatabase database, EventBroker events)
    {
        var model = new ProviderModel(new UnusedProvider(), new LLMModel("model", "unused"));
        var repository = new EventRepository(database);
        var identity = AgentIdentity.Main(id, string.Empty);
        var dependencies = TestModels.Dependencies(identity, events, repository, CancellationToken.None);
        return new AgentSession(
            identity,
            new ModelSelector(model.Selector),
            TestModels.Route(model),
            events,
            repository,
            [],
            TestModels.MaterializePrompt(identity, ".", "."),
            new TodoCollection(id, repository, events),
            new ToolOutputBlobStore(Path.GetTempPath()),
            new Compactor(120_000),
            dependencies.ActiveWorkReminder,
            dependencies.Profile,
            SecurityProfile.Compose(readOnly: false, [], [], []),
            dependencies.Status,
            dependencies.Registry,
            dependencies.Queues,
            CancellationToken.None);
    }

    private static async Task<PermissionPending> WaitForPending(
        PermissionBroker broker,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = broker.Pending();
            if (pending.Count == 1)
            {
                return pending[0];
            }

            await Task.Delay(1, cancellationToken).ConfigureAwait(false);
        }
    }

    private SandboxWriteTarget Target(string name)
    {
        var path = Path.Combine(_root, name);
        File.WriteAllText(path, name);
        return SandboxWriteTarget.Resolve(path);
    }
}

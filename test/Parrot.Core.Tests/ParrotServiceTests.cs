using Grpc.Core;
using Parrot.Agent;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Core.Tests;

// The contract's half of the queue: how a prompt is admitted, what comes back,
// and what is refused.
internal sealed class ParrotServiceTests : IDisposable
{
    private const string Selection = "scripted/model";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "parrot-tests", Guid.NewGuid().ToString("n"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Test]
    public async Task A_prompt_is_answered_with_the_admission_it_made(CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = new ParrotService(Registry(), store, Modes());
        var context = new InProcessServerCallContext(cancellationToken);

        var session = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);
        var admitted = await service.SendMessage(Send(session.Id, "hello", "msg-1"), context);

        _ = await Assert.That(admitted.Created).IsTrue();
        _ = await Assert.That(admitted.MessageId).IsEqualTo("msg-1");
        _ = await Assert.That(admitted.InputId).IsNotEmpty();

        // The same prompt again, as a client that lost its connection would
        // send it: the id it names is the one already admitted.
        var again = await service.SendMessage(Send(session.Id, "hello", "msg-1"), context);

        _ = await Assert.That(again.Created).IsFalse();
        _ = await Assert.That(again.InputId).IsEqualTo(admitted.InputId);
    }

    [Test]
    public async Task Modes_are_listed_created_updated_and_validated(CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = new ParrotService(Registry(), store, Modes());
        var context = new InProcessServerCallContext(cancellationToken);
        var client = new GeneratedParrot.ParrotClient(new InProcessCallInvoker(service));

        var listed = await client.ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken);
        var defaulted = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);
        var created = await service.CreateSession(
            new CreateSessionRequest { Model = Selection, Mode = ModeRegistry.Plan }, context);
        var updated = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Mode = ModeRegistry.Query }, context);
        var carried = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Model = Selection }, context);
        var refused = await Assert.That(async () => await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id, Mode = ModeRegistry.Plan, Model = "unknown/model" },
            context)).Throws<RpcException>();
        var afterRefusal = await service.UpdateSession(
            new UpdateSessionRequest { UserSessionId = created.Id }, context);

        _ = await Assert.That(string.Join(",", listed.Modes.Select(mode => mode.Id)))
            .IsEqualTo("build,plan,query");
        _ = await Assert.That(defaulted.Mode).IsEqualTo(ModeRegistry.Build);
        _ = await Assert.That(created.Mode).IsEqualTo(ModeRegistry.Plan);
        _ = await Assert.That(updated.Mode).IsEqualTo(ModeRegistry.Query);
        _ = await Assert.That(carried.Mode).IsEqualTo(ModeRegistry.Query);
        _ = await Assert.That(refused?.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
        _ = await Assert.That(afterRefusal.Mode).IsEqualTo(ModeRegistry.Query);
    }

    [Test]
    public async Task A_prompt_with_no_delivery_is_refused(CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = new ParrotService(Registry(), store, Modes());
        var context = new InProcessServerCallContext(cancellationToken);

        var session = await service.CreateSession(new CreateSessionRequest { Model = Selection }, context);

        var refused = await Assert.That(async () => await service.SendMessage(
            new SendMessageRequest { UserSessionId = session.Id, Text = "hello" }, context))
            .Throws<RpcException>();

        _ = await Assert.That(refused?.StatusCode).IsEqualTo(StatusCode.InvalidArgument);
    }

    [Test]
    public async Task A_session_nobody_opened_can_be_neither_prompted_nor_interrupted(
        CancellationToken cancellationToken)
    {
        using var store = Store();
        await using var service = new ParrotService(Registry(), store, Modes());
        var context = new InProcessServerCallContext(cancellationToken);

        var prompted = await Assert.That(async () =>
            await service.SendMessage(Send("no-such-session", "hello", "msg-1"), context)).Throws<RpcException>();

        var interrupted = await Assert.That(async () => await service.Interrupt(
            new InterruptRequest { UserSessionId = "no-such-session" }, context)).Throws<RpcException>();

        _ = await Assert.That(prompted?.StatusCode).IsEqualTo(StatusCode.NotFound);
        _ = await Assert.That(interrupted?.StatusCode).IsEqualTo(StatusCode.NotFound);
    }

    private static SendMessageRequest Send(string userSessionId, string text, string messageId) =>
        new()
        {
            UserSessionId = userSessionId,
            Text = text,
            MessageId = messageId,
            Delivery = Delivery.Steer,
        };

    private static ProviderRegistry Registry()
    {
        var provider = new ScriptedProvider("an answer");

        return new ProviderRegistry(
            [provider],
            new Dictionary<string, IReadOnlyList<LLMModel>>(StringComparer.Ordinal)
            {
                ["scripted"] = [new LLMModel("model", "scripted")],
            },
            new ProviderModel(provider, new LLMModel("model", "scripted")));
    }

    private ModeRegistry Modes() => new(Path.Combine(_root, "plans"));

    private SessionStore Store() =>
        new(
            _root,
            Path.Combine(_root, "work"),
            "host",
            new UserSessionFactory(new DirectAgentSessions(), Modes()));
}

using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;

using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class SlashSessionTests
{
    [Test]
    public async Task Compact_forwards_the_current_session_id()
    {
        var invoker = new ScriptedInvoker();
        var session = new SlashSession(
            new GeneratedParrot.ParrotClient(invoker),
            new UserSession { Id = "session-7", Model = "provider/model", Mode = "build" },
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            false,
            new RecordingSlashSessionBinding());

        await session.Compact(CancellationToken.None);

        _ = await Assert.That(invoker.Compactions).Count().IsEqualTo(1);
        _ = await Assert.That(invoker.Compactions[0].UserSessionId).IsEqualTo("session-7");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Start_new_forwards_interaction_ownership_and_cancellation_to_binding(
        bool interactivePermissions)
    {
        using var stopping = new CancellationTokenSource();
        var binding = new RecordingSlashSessionBinding();
        var invoker = new ScriptedInvoker();
        var session = new SlashSession(
            new GeneratedParrot.ParrotClient(invoker),
            new UserSession { Id = "session-0", Model = "provider/model", Mode = "build" },
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            interactivePermissions,
            binding);

        await session.StartNew("provider/model", "plan", stopping.Token);

        _ = await Assert.That(binding.CancellationToken).IsEqualTo(stopping.Token);
        _ = await Assert.That(invoker.Created.Single().InteractivePermissions).IsEqualTo(interactivePermissions);
    }

    private sealed class RecordingSlashSessionBinding : ISlashSessionBinding
    {
        public CancellationToken CancellationToken { get; private set; }

        public Task Replace(UserSession session, CancellationToken cancellationToken)
        {
            CancellationToken = cancellationToken;
            return Task.CompletedTask;
        }
    }
}

using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;

using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class SlashSessionTests
{
    [Test]
    public async Task Start_new_forwards_cancellation_to_binding()
    {
        using var stopping = new CancellationTokenSource();
        var binding = new RecordingSlashSessionBinding();
        var session = new SlashSession(
            new GeneratedParrot.ParrotClient(new ScriptedInvoker()),
            new UserSession { Id = "session-0", Model = "provider/model", Mode = "build" },
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            binding);

        await session.StartNew("provider/model", "plan", stopping.Token);

        _ = await Assert.That(binding.CancellationToken).IsEqualTo(stopping.Token);
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

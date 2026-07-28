using Parrot.Cli.Commands;
using Parrot.Store;

namespace Parrot.Cli.Tests;

internal sealed class SessionsCommandTests : IDisposable
{
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
    public async Task Lists_root_agent_named_and_legacy_sessions_in_creation_order(CancellationToken cancellationToken)
    {
        var index = new SessionIndex(_root);
        index.Publish(Meta("session-later", "main-2", "provider/later", "2026-07-28T02:00:00Z"));
        index.Publish(Meta("session-0", "main", "provider/current", "2026-07-28T01:00:00Z"));
        index.Publish(Meta("session-legacy", string.Empty, "provider/legacy", "2026-07-28T03:00:00Z"));
        var dialog = new TestSlashDialog();
        var command = new SessionsCommand(index, new TestSlashSession("provider/current"), dialog);

        await command.Run(cancellationToken);

        _ = await Assert.That(string.Join('|', dialog.Shown)).IsEqualTo(
            "* main  session-0  provider/current|"
            + "  main-2  session-later  provider/later|"
            + "  <unnamed>  session-legacy  provider/legacy");
    }

    private static SessionMeta Meta(string id, string rootAgentName, string model, string createdAt) =>
        new()
        {
            Id = id,
            WorkingDirectory = "/work",
            HostKey = "host",
            RootAgentName = rootAgentName,
            ProviderId = "provider",
            Model = model,
            CreatedAt = createdAt,
        };
}

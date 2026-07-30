using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class SessionsCommandTests
{
    [Test]
    public async Task Lists_server_sessions_in_creation_order(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        invoker.Sessions.Add(Summary(
            "session-later", "main-2", "provider/later", "2026-07-28T02:00:00Z", SessionState.Inactive));
        invoker.Sessions.Add(Summary(
            "session-0", "main", "provider/current", "2026-07-28T01:00:00Z", SessionState.Active));
        invoker.Sessions.Add(Summary(
            "session-legacy", string.Empty, "provider/legacy", "2026-07-28T03:00:00Z", SessionState.Inactive));
        invoker.Sessions.Add(Summary(
            "session-corrupt", string.Empty, string.Empty, string.Empty, SessionState.Corrupt));
        var dialog = new TestSlashDialog();
        var client = new GeneratedParrot.ParrotClient(invoker);
        var command = new SessionsCommand(client, new TestSlashSession("provider/current"), dialog);

        await command.Run(cancellationToken);

        _ = await Assert.That(string.Join('|', dialog.Shown)).IsEqualTo(
            "  <unnamed>  session-corrupt  <unknown>  corrupt|"
            + "* main  session-0  provider/current  active|"
            + "  main-2  session-later  provider/later  inactive|"
            + "  <unnamed>  session-legacy  provider/legacy  inactive");
    }

    [Test]
    public async Task Older_server_reports_listing_as_unavailable_without_local_fallback(
        CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker { SessionListingUnavailable = true };
        var dialog = new TestSlashDialog();
        var client = new GeneratedParrot.ParrotClient(invoker);
        var command = new SessionsCommand(client, new TestSlashSession("provider/current"), dialog);

        await command.Run(cancellationToken);

        _ = await Assert.That(dialog.Shown).IsEmpty();
        _ = await Assert.That(dialog.Errors).Contains("session listing is unavailable on this server");
    }

    private static SessionSummary Summary(
        string id,
        string rootAgentName,
        string model,
        string createdAt,
        SessionState state) =>
        new()
        {
            UserSessionId = id,
            RootAgentName = rootAgentName,
            Model = model,
            Mode = "build",
            CreatedAt = createdAt,
            State = state,
        };
}

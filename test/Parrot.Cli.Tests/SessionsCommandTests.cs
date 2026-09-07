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
        invoker.Sessions.Add(new SessionSummary
        {
            UserSessionId = "session-later",
            RootAgentName = "main-2",
            Model = "provider/later",
            Mode = "build",
            CreatedAt = "2026-07-28T02:00:00Z",
            State = SessionState.Inactive,
        });
        invoker.Sessions.Add(new SessionSummary
        {
            UserSessionId = "session-0",
            RootAgentName = "main",
            Model = "provider/current",
            Mode = "build",
            CreatedAt = "2026-07-28T01:00:00Z",
            State = SessionState.Active,
        });
        invoker.Sessions.Add(new SessionSummary
        {
            UserSessionId = "session-legacy",
            RootAgentName = string.Empty,
            Model = "provider/legacy",
            Mode = "build",
            CreatedAt = "2026-07-28T03:00:00Z",
            State = SessionState.Inactive,
        });
        invoker.Sessions.Add(new SessionSummary
        {
            UserSessionId = "session-corrupt",
            RootAgentName = string.Empty,
            Model = string.Empty,
            Mode = "build",
            CreatedAt = string.Empty,
            State = SessionState.Corrupt,
        });
        var dialog = new TestSlashDialog();
        var client = new GeneratedParrot.ParrotClient(invoker);
        ISlashCommand command = new SessionsCommand(client, new TestSlashSession("provider/current"), dialog);

        await command.Run(string.Empty, cancellationToken);

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
        ISlashCommand command = new SessionsCommand(client, new TestSlashSession("provider/current"), dialog);

        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(dialog.Shown).IsEmpty();
        _ = await Assert.That(dialog.Errors).Contains("session listing is unavailable on this server");
    }
}

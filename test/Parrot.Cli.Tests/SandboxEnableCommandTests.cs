using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class SandboxEnableCommandTests
{
    [Test]
    [Arguments("false", false)]
    [Arguments("False", false)]
    [Arguments("true", true)]
    [Arguments("TRUE", true)]
    public async Task Command_applies_the_requested_state(string argument, bool expected, CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("model");
        var dialog = new TestSlashDialog();

        await new SandboxEnableCommand(session, dialog).Run(argument, cancellationToken);

        _ = await Assert.That(session.SandboxToggles).Contains(expected);
        _ = await Assert.That(dialog.Errors).IsEmpty();
        _ = await Assert.That(dialog.Shown).Contains(expected ? "sandbox enabled" : "sandbox disabled");
    }

    [Test]
    [Arguments("")]
    [Arguments("maybe")]
    [Arguments("0")]
    public async Task Invalid_argument_shows_usage_and_skips_the_session(string argument)
    {
        var session = new TestSlashSession("model");
        var dialog = new TestSlashDialog();

        await new SandboxEnableCommand(session, dialog).Run(argument, CancellationToken.None);

        _ = await Assert.That(session.SandboxToggles).IsEmpty();
        _ = await Assert.That(dialog.Errors).Contains("usage: /sandbox_enable <true|false>");
    }

    [Test]
    public async Task Reported_state_comes_from_the_host_response(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("model") { SandboxEnabledResponse = false };
        var dialog = new TestSlashDialog();

        await new SandboxEnableCommand(session, dialog).Run("true", cancellationToken);

        _ = await Assert.That(session.SandboxToggles).Contains(true);
        _ = await Assert.That(dialog.Shown).Contains("sandbox disabled");
    }
}

using Parrot.Cli.Commands;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class ModeCommandTests
{
    [Test]
    public async Task Mode_and_clear_follow_the_dialog_selection(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        var client = new GeneratedParrot.ParrotClient(invoker);
        var session = new TestSlashSession("provider/old");
        var activity = new TestSlashActivity();
        var modeDialog = new TestSlashDialog().Select("plan");
        var clearDialog = new TestSlashDialog().Select("provider", "model", "query");

        await new ModeCommand(new ModeSelection(client, modeDialog), session, activity, modeDialog)
            .Run(string.Empty, cancellationToken);
        await new ClearCommand(
            new ModelWizard(client, clearDialog),
            new ModeSelection(client, clearDialog),
            session,
            activity,
            clearDialog).Run(string.Empty, cancellationToken);
        await new ModesCommand(client, modeDialog).Run(string.Empty, cancellationToken);

        _ = await Assert.That(session.Id).IsEqualTo("session-1");
        _ = await Assert.That(session.Model).IsEqualTo("provider/model");
        _ = await Assert.That(session.Mode).IsEqualTo("query");
        _ = await Assert.That(activity.Waits).IsEqualTo(2);
        _ = await Assert.That(modeDialog.Shown)
            .Contains("mode is now plan").And.Contains("build").And.Contains("query");
        _ = await Assert.That(clearDialog.Shown).Contains("new session session-1");
    }

    [Test]
    public async Task Empty_model_and_mode_catalogs_report_errors_without_opening_a_picker(
        CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        invoker.Models.Clear();
        invoker.Modes.Clear();
        var client = new GeneratedParrot.ParrotClient(invoker);
        var dialog = new TestSlashDialog();

        _ = await new ModelWizard(client, dialog).Select(null, cancellationToken);
        _ = await new ModeSelection(client, dialog).Select(cancellationToken);

        _ = await Assert.That(string.Join('|', dialog.Errors))
            .IsEqualTo("no providers are configured|no modes are available");
    }
}

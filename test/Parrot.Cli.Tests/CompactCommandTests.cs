using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class CompactCommandTests
{
    [Test]
    public async Task Bare_compact_waits_for_idle_and_compacts_once_with_the_same_token(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog();

        ISlashCommand command = new CompactCommand(session, activity, dialog);
        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(activity.Waits).IsEqualTo(1);
        _ = await Assert.That(activity.CancellationToken).IsEqualTo(cancellationToken);
        _ = await Assert.That(session.Compactions).IsEqualTo(1);
        _ = await Assert.That(session.CompactionCancellationToken).IsEqualTo(cancellationToken);
        _ = await Assert.That(dialog.Errors).IsEmpty();
    }

    [Test]
    [Arguments("requested history")]
    [Arguments(" ")]
    public async Task Arguments_are_rejected_without_waiting_or_compacting(string arguments, CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        var activity = new TestSlashActivity();
        var dialog = new TestSlashDialog();

        ISlashCommand command = new CompactCommand(session, activity, dialog);
        await command.Run(arguments, cancellationToken);

        _ = await Assert.That(activity.Waits).IsEqualTo(0);
        _ = await Assert.That(session.Compactions).IsEqualTo(0);
        _ = await Assert.That(dialog.Errors).Contains("usage: /compact");
    }
}

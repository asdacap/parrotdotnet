using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class GoalCommandTests
{
    [Test]
    public async Task Run_sets_nonempty_goal_and_clears_bare_goal(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        var dialog = new TestSlashDialog();
        ISlashCommand command = new GoalCommand(session, dialog);

        await command.Run("ship v1", cancellationToken);
        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(session.Goals.Count).IsEqualTo(1);
        _ = await Assert.That(session.Goals[0]).IsEqualTo("ship v1");
        _ = await Assert.That(session.ClearedGoals).IsEqualTo(1);
        _ = await Assert.That(string.Join('|', dialog.Shown)).IsEqualTo("goal set: ship v1|goal cleared");
    }
}

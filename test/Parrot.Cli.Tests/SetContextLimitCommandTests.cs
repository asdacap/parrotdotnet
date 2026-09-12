using Parrot.Cli.Commands;
using Parrot.Protocol;

namespace Parrot.Cli.Tests;

internal sealed class SetContextLimitCommandTests
{
    [Test]
    public async Task Command_reports_when_alias_override_remains_effective(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("alias")
        {
            ContextLimitResponse = new SetContextLimitResponse
            {
                ContextLimit = "100k",
                AliasOverride = true,
            },
        };
        var dialog = new TestSlashDialog();

        await new SetContextLimitCommand(session, dialog).Run("100k", cancellationToken);

        _ = await Assert.That(dialog.Shown).Contains(
            "Context compaction limit set to 100k; the current model alias override remains effective");
        _ = await Assert.That(session.ContextLimits).Contains("100k");
    }

    [Test]
    public async Task Invalid_value_does_not_call_session(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("alias");
        var dialog = new TestSlashDialog();

        await new SetContextLimitCommand(session, dialog).Run("not-a-size", cancellationToken);

        _ = await Assert.That(session.ContextLimits).IsEmpty();
        _ = await Assert.That(dialog.Errors).Contains("usage: /set-context-limit <size>");
    }
}

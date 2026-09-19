using Grpc.Core;
using Parrot.Cli.Commands;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class StatusCommandTests
{
    [Test]
    public async Task Command_prints_the_agent_status_and_usage_lines(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker { StatusText = "Model: provider/model", UsageLines = ["Credits: 5.00"] };
        var dialog = new TestSlashDialog();
        var client = new GeneratedParrot.ParrotClient(invoker);
        ISlashCommand command = new StatusCommand(client, new TestSlashSession("provider/model"), dialog);

        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(dialog.Shown).IsEmpty();
        _ = await Assert.That(invoker.Statuses).HasSingleItem();
        _ = await Assert.That(invoker.Statuses[0].UserSessionId).IsEqualTo("session-0");
        _ = await Assert.That(string.Join('|', dialog.Printed)).IsEqualTo("Model: provider/model|Credits: 5.00");
    }

    [Test]
    public async Task Empty_status_and_usage_fall_back_to_a_placeholder(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker { StatusText = string.Empty, UsageLines = [] };
        var dialog = new TestSlashDialog();
        var client = new GeneratedParrot.ParrotClient(invoker);
        ISlashCommand command = new StatusCommand(client, new TestSlashSession("provider/model"), dialog);

        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(string.Join('|', dialog.Printed)).IsEqualTo("no status is currently available");
    }

    [Test]
    public async Task Unimplemented_server_shows_an_unavailability_error(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker { StatusFailure = StatusCode.Unimplemented };
        var dialog = new TestSlashDialog();
        var client = new GeneratedParrot.ParrotClient(invoker);
        ISlashCommand command = new StatusCommand(client, new TestSlashSession("provider/model"), dialog);

        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(dialog.Errors).Contains("session status is unavailable on this server");
        _ = await Assert.That(dialog.Shown).IsEmpty();
    }
}

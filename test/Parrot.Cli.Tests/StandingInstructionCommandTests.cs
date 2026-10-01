using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class StandingInstructionCommandTests
{
    [Test]
    public async Task Run_sets_nonempty_instruction_and_clears_with_bare_command(CancellationToken cancellationToken)
    {
        var session = new TestSlashSession("provider/model");
        ISlashCommand command = new StandingInstructionCommand(session);

        await command.Run("be terse", cancellationToken);
        await command.Run(string.Empty, cancellationToken);

        _ = await Assert.That(session.StandingInstructions).IsEquivalentTo(["be terse", string.Empty]);
    }
}

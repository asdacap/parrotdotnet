using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced;

namespace Parrot.Cli.Tests;

internal sealed class SlashCommandCompletionTests
{
    [Test]
    public async Task Completion_filters_wraps_preserves_selection_and_accepts_the_command_token()
    {
        var first = new CompletionCommand("/mode");
        var second = new CompletionCommand("/model");
        var completion = new SlashCommandCompletion(new SlashCommandRegistry([second, first], new TestSlashDialog()));

        completion.Refresh("/mo");
        completion.SelectNext();
        completion.Refresh("/mod");

        _ = await Assert.That(string.Join('|', completion.Commands.Select(command => command.Name)))
            .IsEqualTo("/mode|/model");
        _ = await Assert.That(completion.Selected).IsEqualTo(1);
        _ = await Assert.That(completion.Accept("/mod trailing text")).IsEqualTo("/model trailing text");

        completion.SelectNext();
        _ = await Assert.That(completion.Selected).IsEqualTo(0);
        completion.SelectPrevious();
        _ = await Assert.That(completion.Selected).IsEqualTo(1);

        completion.Refresh("/model arguments");
        _ = await Assert.That(completion.Commands).IsEmpty();

        completion.Refresh("normal prompt");
        _ = await Assert.That(completion.Commands).IsEmpty();
        _ = await Assert.That(completion.Accept("/unknown")).IsNull();
    }

    private sealed class CompletionCommand(string name) : ISlashCommand
    {
        public string Name { get; } = name;

        public string Summary => "Completion command";

        public Task Run(string arguments, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

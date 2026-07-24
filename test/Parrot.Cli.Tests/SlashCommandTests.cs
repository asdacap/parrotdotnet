using Parrot.Cli.Commands;

namespace Parrot.Cli.Tests;

internal sealed class SlashCommandTests
{
    [Test]
    [Arguments("/help")]
    [Arguments("/exit")]
    [Arguments("/version")]
    [Arguments("/model")]
    [Arguments("/models")]
    [Arguments("/mode")]
    [Arguments("/modes")]
    [Arguments("/sessions")]
    [Arguments("/clear")]
    [Arguments("/auth")]
    public async Task Every_registered_command_resolves_by_name(string name)
    {
        var registry = Registry();

        _ = await Assert.That(registry.Find(name)).IsNotNull();
    }

    [Test]
    [Arguments("/nope")]
    [Arguments("/mdoel")]
    [Arguments("/")]

    // It must resolve to nothing rather than fall through to the model: a
    // mistyped command is a mistake, not a prompt worth paying for.
    public async Task An_unknown_command_resolves_to_nothing(string name) =>
        _ = await Assert.That(Registry().Find(name)).IsNull();

    [Test]
    public async Task Every_command_carries_a_summary_for_help()
    {
        foreach (var command in Registry().Commands)
        {
            _ = await Assert.That(command.Summary).IsNotEmpty();
            _ = await Assert.That(command.Name).StartsWith("/");
        }
    }

    [Test]
    public async Task Exit_is_the_only_command_that_ends_the_loop()
    {
        var ending = Registry().Commands.Where(command => command is ExitCommand).ToList();

        _ = await Assert.That(ending).HasSingleItem();
        _ = await Assert.That(ending[0].Name).IsEqualTo("/exit");
    }

    private static SlashCommandRegistry Registry()
    {
        var commands = new List<ISlashCommand>
        {
            new ExitCommand(),
            new VersionCommand(),
            new ModelCommand(),
            new ModelsCommand(),
            new ModeCommand(),
            new ModesCommand(),
            new SessionsCommand(),
            new ClearCommand("model"),
            new AuthCommand(static () => string.Empty),
        };

        var registry = new SlashCommandRegistry(commands);
        commands.Add(new HelpCommand(registry));

        return registry;
    }
}

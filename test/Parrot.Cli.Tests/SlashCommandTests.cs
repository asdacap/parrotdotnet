using Parrot.Auth;
using Parrot.Cli.Commands;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class SlashCommandTests
{
    [Test]
    public async Task Factory_registers_every_command()
    {
        using var application = new CancellationTokenSource();
        using var http = new HttpClient();
        var registry = SlashCommands.Create(
            new GeneratedParrot.ParrotClient(new ScriptedInvoker()),
            new TestSlashDialog(),
            new TestSlashSession("provider/old"),
            new TestSlashActivity(),
            new ApplicationExit(application),
            new UnusedCredentials(),
            new OpenAiOAuthClient(http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            ["provider"]);

        _ = await Assert.That(string.Join('|', registry.Commands.Select(command => command.Name)))
            .IsEqualTo("/auth|/clear|/effort|/exit|/goal|/help|/mode|/model|/model-alias|/models|/modes|/sessions|/version");
        _ = await Assert.That(registry.Commands.All(command => command.Summary.Length > 0)).IsTrue();
    }

    [Test]
    public async Task Registry_completes_the_first_slash_token_in_ordinal_order()
    {
        var dialog = new TestSlashDialog();
        var registry = new SlashCommandRegistry(
            [new CompletionCommand("/model"), new CompletionCommand("/mode"), new CompletionCommand("/exit")], dialog);

        _ = await Assert.That(string.Join('|', registry.Complete("/").Select(command => command.Name)))
            .IsEqualTo("/exit|/mode|/model");
        _ = await Assert.That(string.Join('|', registry.Complete("/mo").Select(command => command.Name)))
            .IsEqualTo("/mode|/model");
        _ = await Assert.That(string.Join('|', registry.Complete("/mo ignored").Select(command => command.Name)))
            .IsEqualTo("/mode|/model");
        _ = await Assert.That(registry.Complete("normal prompt")).IsEmpty();
        _ = await Assert.That(registry.Complete("/unknown")).IsEmpty();
    }

    [Test]
    public async Task Registry_dispatches_by_first_token_and_normalizes_arguments(CancellationToken cancellationToken)
    {
        var dialog = new TestSlashDialog();
        var command = new TestSlashCommand();
        var registry = new SlashCommandRegistry([command], dialog);

        await registry.Dispatch("/test", cancellationToken);
        await registry.Dispatch("/test   spaces", cancellationToken);
        await registry.Dispatch("/test\t\t tabs", cancellationToken);
        await registry.Dispatch("/test multiword remainder", cancellationToken);
        await registry.Dispatch("/unknown arguments", cancellationToken);

        _ = await Assert.That(command.Runs).IsEqualTo(4);
        _ = await Assert.That(string.Join('|', command.Arguments)).IsEqualTo(string.Join('|', [string.Empty, "spaces", "tabs", "multiword remainder"]));
        _ = await Assert.That(registry.Find("/test")).IsSameReferenceAs(command);
        _ = await Assert.That(registry.Find("/unknown")).IsNull();
        _ = await Assert.That(string.Join('|', dialog.Errors)).IsEqualTo("unknown command /unknown, try /help");
    }

    private sealed class CompletionCommand(string name) : ISlashCommand
    {
        public string Name { get; } = name;

        public string Summary => "Completion command";

        public Task Run(string arguments, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

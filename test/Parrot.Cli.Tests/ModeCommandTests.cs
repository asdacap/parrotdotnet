using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class ModeCommandTests : IDisposable
{
    private readonly StringReader _input = new(string.Empty);
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly HttpClient _http = new();

    [Test]
    public async Task Mode_commands_and_clear_use_the_protocol_contract(CancellationToken cancellationToken)
    {
        var invoker = new ScriptedInvoker();
        var context = Context(invoker, "query");

        _ = await new ModeCommand().Run(context, "plan", cancellationToken);
        _ = await Assert.That(context.Mode).IsEqualTo("plan");
        _ = await new ModesCommand().Run(context, string.Empty, cancellationToken);
        _ = await new ClearCommand("default-model").Run(context, "selected-model", cancellationToken);

        _ = await Assert.That(invoker.Updated).HasSingleItem();
        _ = await Assert.That(invoker.Updated[0].UserSessionId).IsEqualTo("user-session");
        _ = await Assert.That(invoker.Updated[0].Mode).IsEqualTo("plan");
        _ = await Assert.That(invoker.Created).HasSingleItem();
        _ = await Assert.That(invoker.Created[0].Model).IsEqualTo("selected-model");
        _ = await Assert.That(invoker.Created[0].Mode).IsEqualTo("build");
        _ = await Assert.That(context.UserSessionId).IsEqualTo("session-1");
        _ = await Assert.That(context.Mode).IsEqualTo("build");
        _ = await Assert.That(_output.ToString()).Contains("  build").And.Contains("  plan").And.Contains("  query");
    }

    public void Dispose()
    {
        _input.Dispose();
        _output.Dispose();
        _error.Dispose();
        _http.Dispose();
    }

    private SlashContext Context(ScriptedInvoker invoker, string mode) =>
        new(
            new GeneratedParrot.ParrotClient(invoker),
            new UnusedCredentials(),
            new OpenAiOAuthClient(_http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-mode-command-tests.yaml")),
            [],
            "user-session",
            "provider/model",
            mode,
            _input,
            _output,
            _error);
}

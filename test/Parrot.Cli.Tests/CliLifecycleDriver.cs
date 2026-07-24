using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class CliLifecycleDriver : IDisposable
{
    private readonly bool _enhanced;
    private readonly StringWriter _output = new();
    private readonly StringWriter _error = new();
    private readonly HttpClient _http = new();

    public CliLifecycleDriver(bool enhanced)
    {
        _enhanced = enhanced;
        Interrupts = new Interrupts(Stopping);
    }

    public ScriptedInvoker Invoker { get; } = new();

    public ScriptedInput Input { get; } = new();

    public CancellationTokenSource Stopping { get; } = new();

    public Interrupts Interrupts { get; }

    public async Task Sent(int count, CancellationToken cancellationToken)
    {
        while (Invoker.Sent.Count < count)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    public Task<int> Drive(CancellationToken cancellationToken)
    {
        var client = new GeneratedParrot.ParrotClient(Invoker);

        var context = new SlashContext(
            client,
            new UnusedCredentials(),
            new OpenAiOAuthClient(_http, new UnusedBrowser(), new OpenAiOAuthOptions()),
            new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
            ["provider"],
            "user-session",
            "provider/model",
            "build",
            Input,
            _output,
            _error);

        return _enhanced
            ? new EnhancedCli(client, new SlashCommandRegistry([new ExitCommand()]), Interrupts)
                .Run(context, string.Empty, Input, _output, cancellationToken)
            : new BasicCli(client, new SlashCommandRegistry([new ExitCommand()]), Interrupts)
                .Run(context, string.Empty, Input, _output, cancellationToken);
    }

    public void Dispose()
    {
        Input.Dispose();
        _output.Dispose();
        _error.Dispose();
        _http.Dispose();
        Stopping.Dispose();
    }
}

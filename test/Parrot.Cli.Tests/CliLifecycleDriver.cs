using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced;
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

    public async Task OutputContains(string text, CancellationToken cancellationToken)
    {
        while (!_output.ToString().Contains(text, StringComparison.Ordinal))
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ErrorContains(string text, CancellationToken cancellationToken)
    {
        while (!_error.ToString().Contains(text, StringComparison.Ordinal))
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

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
        var commands = new SlashCommandRegistry([new ExitCommand()]);
        var credentials = new UnusedCredentials();
        var oauth = new OpenAiOAuthClient(_http, new UnusedBrowser(), new OpenAiOAuthOptions());
        var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
        var request = new EnhancedChatRequest(
            new() { Model = "provider/model", Mode = "build" },
            string.Empty);
        var terminal = new TestTerminal(Input, _output, _error);

        return _enhanced
            ? new EnhancedCli(
                client,
                commands,
                Interrupts,
                request,
                new EnhancedSlashContextFactory(
                    client,
                    credentials,
                    oauth,
                    configuration,
                    ["provider"],
                    terminal),
                terminal).Run(cancellationToken)
            : new BasicCli(
                client,
                commands,
                Interrupts,
                credentials,
                oauth,
                configuration,
                ["provider"],
                "provider/model",
                "build",
                string.Empty,
                false,
                Input,
                _output,
                _error).Run(cancellationToken);
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

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

    public EnhancedCli CreateEnhancedCli() =>
        CreateEnhancedCli(new TestTerminal(Input, _output, _error, 80));

    public EnhancedCli CreateEnhancedCli(ITerminal terminal)
    {
        var client = new GeneratedParrot.ParrotClient(Invoker);
        return new EnhancedCli(
            client,
            new SlashCommandRegistry([new ExitCommand()]),
            Interrupts,
            new EnhancedChatRequest(
                new() { Model = "provider/model", Mode = "build" },
                string.Empty),
            new EnhancedSlashContextFactory(
                client,
                new UnusedCredentials(),
                new OpenAiOAuthClient(_http, new UnusedBrowser(), new OpenAiOAuthOptions()),
                new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
                ["provider"],
                terminal),
            terminal);
    }

    public Task<int> Drive(CancellationToken cancellationToken)
    {
        var client = new GeneratedParrot.ParrotClient(Invoker);

        return _enhanced
            ? CreateEnhancedCli().Run(cancellationToken)
            : new BasicCli(
                client,
                new SlashCommandRegistry([new ExitCommand()]),
                Interrupts,
                new UnusedCredentials(),
                new OpenAiOAuthClient(_http, new UnusedBrowser(), new OpenAiOAuthOptions()),
                new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml")),
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

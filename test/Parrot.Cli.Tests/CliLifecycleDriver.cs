using Parrot.Auth;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class CliLifecycleDriver : IDisposable
{
    private readonly bool _enhanced;
    private readonly EnhancedChatRequest _enhancedRequest;
    private readonly Func<TimeSpan, CancellationToken, Task> _delaySubmit;
    private readonly SynchronizedStringWriter _output = new();
    private readonly SynchronizedStringWriter _error = new();

    public CliLifecycleDriver(bool enhanced)
        : this(
            enhanced,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty))
    {
    }

    public CliLifecycleDriver(bool enhanced, EnhancedChatRequest enhancedRequest)
        : this(enhanced, enhancedRequest, static (_, token) => Task.Delay(1, token))
    {
    }

    public CliLifecycleDriver(
        bool enhanced,
        EnhancedChatRequest enhancedRequest,
        Func<TimeSpan, CancellationToken, Task> delaySubmit)
    {
        _enhanced = enhanced;
        _enhancedRequest = enhancedRequest;
        _delaySubmit = delaySubmit;
        Interrupts = new Interrupts(Stopping);
    }

    public ScriptedInvoker Invoker { get; } = new();

    public ScriptedInput Input { get; } = new();

    public CancellationTokenSource Stopping { get; } = new();

    public Interrupts Interrupts { get; }

    public HttpClient Http { get; } = new();

    public string Output => _output.Snapshot();

    public async Task OutputContainsAfter(int start, string text, CancellationToken cancellationToken)
    {
        while (_output.Snapshot()[start..].Contains(text, StringComparison.Ordinal) is false)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task OutputContains(string text, CancellationToken cancellationToken)
    {
        while (!_output.Snapshot().Contains(text, StringComparison.Ordinal))
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task ErrorContains(string text, CancellationToken cancellationToken)
    {
        while (!_error.Snapshot().Contains(text, StringComparison.Ordinal))
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
        if (_enhanced)
        {
            var configuration = new Configuration(Path.Combine(Path.GetTempPath(), "parrot-tests-config.yaml"));
            var terminal = new TestTerminal(Input, _output, _error, 80);
            var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
            var renderer = new EnhancedTurnRenderer(terminal, configuration, presenters);
            return new EnhancedCli(
                client,
                Interrupts,
                _enhancedRequest,
                new UnusedCredentials(),
                new OpenAiOAuthClient(Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
                configuration,
                ["provider"],
                terminal,
                presenters,
                renderer,
                _delaySubmit).Run(cancellationToken);
        }

        return new BasicCli(
                client,
                Interrupts,
                new UnusedCredentials(),
                new OpenAiOAuthClient(Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
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
        Http.Dispose();
        Stopping.Dispose();
    }

    private sealed class SynchronizedStringWriter : StringWriter
    {
        private readonly object _sync = new();

        public string Snapshot()
        {
            lock (_sync)
            {
                return GetStringBuilder().ToString();
            }
        }

        public override Task WriteAsync(
            ReadOnlyMemory<char> buffer,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                Write(buffer.Span);
            }

            return Task.CompletedTask;
        }
    }
}

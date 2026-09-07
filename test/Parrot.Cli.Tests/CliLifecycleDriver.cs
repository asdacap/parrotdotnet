using Parrot.Agent;
using Parrot.Auth;
using Parrot.Cli.Enhanced;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Tools;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Tests;

internal sealed class CliLifecycleDriver : IDisposable
{
    private readonly TransportDiagnosticsFixture _diagnostics = new();
    private readonly bool _enhanced;
    private readonly EnhancedChatRequest _enhancedRequest;
    private readonly Func<TimeSpan, CancellationToken, Task> _delaySubmit;
    private readonly Configuration _configuration;
    private readonly string _configurationDirectory;
    private readonly TimeProvider _timeProvider;
    private readonly int _columns;
    private readonly SynchronizedStringWriter _output = new();
    private readonly SynchronizedStringWriter _error = new();
    private TestTerminal? _terminal;

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

    public CliLifecycleDriver(bool enhanced, string configurationContent)
        : this(
            enhanced,
            new EnhancedChatRequest(new() { Model = "provider/model", Mode = "build" }, string.Empty),
            static (_, token) => Task.Delay(1, token),
            LoadConfiguration(configurationContent),
            TimeProvider.System,
            80)
    {
    }

    public CliLifecycleDriver(
        bool enhanced,
        EnhancedChatRequest enhancedRequest,
        Func<TimeSpan, CancellationToken, Task> delaySubmit)
        : this(enhanced, enhancedRequest, delaySubmit, LoadConfiguration(string.Empty), TimeProvider.System, 80)
    {
    }

    public CliLifecycleDriver(
        bool enhanced,
        EnhancedChatRequest enhancedRequest,
        Func<TimeSpan, CancellationToken, Task> delaySubmit,
        TimeProvider timeProvider,
        int columns)
        : this(enhanced, enhancedRequest, delaySubmit, LoadConfiguration(string.Empty), timeProvider, columns)
    {
    }

    private CliLifecycleDriver(
        bool enhanced,
        EnhancedChatRequest enhancedRequest,
        Func<TimeSpan, CancellationToken, Task> delaySubmit,
        (Configuration Configuration, string Directory) loadedConfiguration,
        TimeProvider timeProvider,
        int columns)
    {
        _enhanced = enhanced;
        _enhancedRequest = enhancedRequest;
        _delaySubmit = delaySubmit;
        _configuration = loadedConfiguration.Configuration;
        _configurationDirectory = loadedConfiguration.Directory;
        _timeProvider = timeProvider;
        _columns = columns;
        Interrupts = new Interrupts(Stopping);
    }

    public ScriptedInvoker Invoker { get; } = new();

    public ScriptedInput Input { get; } = new();

    public CancellationTokenSource Stopping { get; } = new();

    public Interrupts Interrupts { get; }

    public HttpClient Http { get; } = new();

    public string Output => _output.Snapshot();

    public bool InputRedirected { get; init; }

    public void Resize(int columns) =>
        (_terminal ?? throw new InvalidOperationException("the enhanced terminal is not running")).Resize(columns);

    public async Task OutputContainsAfter(int start, string text, CancellationToken cancellationToken)
    {
        while (_output.Snapshot()[start..].Contains(text, StringComparison.Ordinal) is false)
        {
            await Task.Delay(5, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string> FlushedOutputContainsAfter(
        int start,
        string text,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var flushed = _output.FlushedSnapshot();
            if (flushed.Length >= start && flushed[start..].Contains(text, StringComparison.Ordinal))
            {
                return flushed[start..];
            }

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
            var terminal = new TestTerminal(Input, _output, _error, _columns);
            _terminal = terminal;
            var presenters = new ToolPresenterRegistry([], new GenericToolPresenter());
            var renderer = new EnhancedTurnRenderer(terminal, _configuration, presenters);
            return new EnhancedCli(
                client,
                Interrupts,
                _enhancedRequest,
                new UnusedCredentials(),
                new OpenAiOAuthClient(Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
                _configuration,
                ["provider"],
                terminal,
                presenters,
                renderer,
                _timeProvider,
                _delaySubmit,
                Attachments(_configuration),
                _diagnostics.Log).Run(cancellationToken);
        }

        return new BasicCli(
                client,
                Interrupts,
                new UnusedCredentials(),
                new OpenAiOAuthClient(Http, new UnusedBrowser(), new OpenAiOAuthOptions()),
                _configuration,
                ["provider"],
                "provider/model",
                "build",
                _enhancedRequest.Prompt,
                InputRedirected,
                Input,
                _output,
                _error,
                Attachments(_configuration),
                _diagnostics.Log)
        { InitialSession = _enhancedRequest.InitialSession }.Run(cancellationToken);
    }

    public void Dispose()
    {
        _diagnostics.Dispose();
        Input.Dispose();
        _output.Dispose();
        _error.Dispose();
        Http.Dispose();
        Stopping.Dispose();
        Directory.Delete(_configurationDirectory, recursive: true);
    }

    private static (Configuration Configuration, string Directory) LoadConfiguration(string content)
    {
        var root = Path.Combine(Path.GetTempPath(), "parrot-cli-tests", Guid.NewGuid().ToString("N"));
        _ = Directory.CreateDirectory(root);
        var path = Path.Combine(root, "config.yaml");
        File.WriteAllText(path, content);
        return (
            Configuration.Load(path, Path.Combine(root, "predefined_config.yaml")),
            root);
    }

    private static PromptAttachmentUploader Attachments(Configuration configuration)
    {
        var profiles = new Dictionary<string, ProfileConfig>(StringComparer.Ordinal)
        {
            [ModeRegistry.Build] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
            [ModeRegistry.Plan] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
            [ModeRegistry.Query] = new(string.Empty, string.Empty, null, 1, 1, false, false, true, false, []),
        };
        var registry = new ProfileRegistry(profiles, [], [], configuration.DisabledTools);
        return new PromptAttachmentUploader(
            new ToolWorkspace(Directory.GetCurrentDirectory()),
            new ModeRegistry(registry, ModeRegistry.Build));
    }

    private sealed class SynchronizedStringWriter : StringWriter
    {
        private readonly object _sync = new();
        private int _flushedLength;

        public string Snapshot()
        {
            lock (_sync)
            {
                return GetStringBuilder().ToString();
            }
        }

        public string FlushedSnapshot()
        {
            lock (_sync)
            {
                return GetStringBuilder().ToString(0, _flushedLength);
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

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_sync)
            {
                _flushedLength = GetStringBuilder().Length;
            }

            return Task.CompletedTask;
        }
    }
}

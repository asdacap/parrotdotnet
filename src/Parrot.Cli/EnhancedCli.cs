using System.Threading.Channels;
using Grpc.Core;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.State;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class EnhancedCli(
    GeneratedParrot.ParrotClient client,
    SlashCommandRegistry commands,
    Interrupts interrupts,
    Func<int> columns,
    Func<IRawTerminal?> rawTerminal,
    Func<bool> color) : IInterruptListener
{
    private const string Prompt = "> ";

    private readonly Channel<bool> _interrupts =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile bool _busy;
    private volatile bool _interruptRequested;

    public EnhancedCli(
        GeneratedParrot.ParrotClient client,
        SlashCommandRegistry commands,
        Interrupts interrupts)
        : this(
            client,
            commands,
            interrupts,
            static () => Console.WindowWidth,
            static () => string.Equals(
                Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal)
                ? null
                : UnixRawTerminal.Open(),
            static () => Environment.GetEnvironmentVariable("NO_COLOR") is null)
    {
    }

    public static async Task<int> Drive(
        GeneratedParrot.ParrotClient client,
        SlashCommandRegistry commands,
        Interrupts interrupts,
        StatePaths paths,
        Configuration configuration,
        OpenAiOAuthClient oauthClient,
        string model,
        string prompt,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var text = prompt;

        if (text.Length == 0 && Console.IsInputRedirected)
        {
            text = (await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (text.Length == 0)
            {
                return CommandDispatcher.ExitUsage;
            }
        }

        using var credentials = new FileCredentialStore(paths.CredentialsFile);

        UserSession session;

        try
        {
            session = await client.CreateSessionAsync(
                new CreateSessionRequest { Model = model }, cancellationToken: cancellationToken);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
        {
            await error.WriteLineAsync($"parrot: {failure.Status.Detail}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return CommandDispatcher.ExitFailure;
        }

        var context = new SlashContext(
            client,
            credentials,
            oauthClient,
            configuration,
            ProviderRegistryBuilder.BuildableProviderIds(configuration),
            session.Id,
            session.Model,
            input,
            output,
            error);

        var cli = new EnhancedCli(client, commands, interrupts);
        return await cli.Run(context, text, input, output, cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> Run(
        SlashContext context,
        string prompt,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        return prompt.Length > 0
            ? await Once(context, prompt, output, cancellationToken).ConfigureAwait(false)
            : await Loop(context, input, output, cancellationToken).ConfigureAwait(false);
    }

    public bool Interrupted()
    {
        if (!_busy || _interruptRequested)
        {
            return false;
        }

        _interruptRequested = true;
        return _interrupts.Writer.TryWrite(true);
    }

    internal static async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        TextWriter output,
        TextWriter error,
        Func<int> columns,
        CancellationToken cancellationToken,
        Func<Event, CancellationToken, Task>? beforeRender = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var view = new TurnView(output, error, columns);
        var waitingForVisibleEvent = beforeRender is not null;
        var turnStarted = false;

        try
        {
            while (await stream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                turnStarted |= stream.Current.PayloadCase == Event.PayloadOneofCase.TurnStarted;
                if (waitingForVisibleEvent &&
                    beforeRender is { } callback &&
                    IsVisible(stream.Current, turnStarted))
                {
                    waitingForVisibleEvent = false;
                    await callback(stream.Current, cancellationToken).ConfigureAwait(false);
                }

                var completed = await view.Render(stream.Current, cancellationToken).ConfigureAwait(false);
                if (completed is not null)
                {
                    return completed.Value;
                }
            }

            await view.End(cancellationToken).ConfigureAwait(false);
            return false;
        }
        catch (OperationCanceledException)
        {
            await view.Cancel(CancellationToken.None).ConfigureAwait(false);
            return false;
        }
    }

    private static bool IsVisible(Event published, bool turnStarted) => published.PayloadCase switch
    {
        Event.PayloadOneofCase.InputAdmitted => turnStarted,
        Event.PayloadOneofCase.None or
        Event.PayloadOneofCase.TurnStarted or
        Event.PayloadOneofCase.ToolCallChunk or
        Event.PayloadOneofCase.InputPromoted or
        Event.PayloadOneofCase.RetryNotice => false,
        _ => true,
    };

    private static SendMessageRequest Message(string userSessionId, string text) =>
        new()
        {
            UserSessionId = userSessionId,
            Text = text,
            Delivery = Delivery.Steer,
        };

    private static Task DrawEditor(
        TerminalFrameRenderer renderer,
        PromptValue prompt,
        SlashContext context,
        CancellationToken cancellationToken) =>
        renderer.Draw(
            new TerminalFrame([], null, new ModelineValue("chat", "ready", context.Model), prompt),
            cancellationToken);

    private async Task<int> Once(
        SlashContext context, string prompt, TextWriter output, CancellationToken cancellationToken)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var call = client.Listen(
            new ListenRequest { UserSessionId = context.UserSessionId }, cancellationToken: listening.Token);

        _ = await client.SendMessageAsync(
            Message(context.UserSessionId, prompt), cancellationToken: cancellationToken);

        var completed = await RenderTurn(call.ResponseStream, output, context.Error, columns, listening.Token)
            .ConfigureAwait(false);

        await listening.CancelAsync().ConfigureAwait(false);
        return completed ? CommandDispatcher.ExitSuccess : CommandDispatcher.ExitFailure;
    }

    private async Task<int> Loop(
        SlashContext context, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        await output.WriteLineAsync(
            $"parrot {BuildInfo.Version} — /help for commands, /exit to leave".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        using var terminal = rawTerminal();
        if (terminal is not null)
        {
            return await RawLoop(context, output, terminal, cancellationToken).ConfigureAwait(false);
        }

        return await LineLoop(context, input, output, cancellationToken).ConfigureAwait(false);
    }

    private async Task<int> LineLoop(
        SlashContext context, TextReader input, TextWriter output, CancellationToken cancellationToken)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var streaming = CancellationTokenSource.CreateLinkedTokenSource(listening.Token);
        var listeningTo = context.UserSessionId;
        var call = client.Listen(
            new ListenRequest { UserSessionId = listeningTo }, cancellationToken: streaming.Token);
        var rendering = Task.CompletedTask;
        var interrupting = Interrupting(context, listening.Token);

        interrupts.Install(this);

        try
        {
            await Ready(output, cancellationToken).ConfigureAwait(false);

            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                var entered = line.Trim();
                if (entered.Length == 0)
                {
                    await Ready(output, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (entered.StartsWith('/'))
                {
                    if (await Dispatch(context, entered, cancellationToken).ConfigureAwait(false) == SlashOutcome.Exit)
                    {
                        break;
                    }

                    if (!string.Equals(context.UserSessionId, listeningTo, StringComparison.Ordinal))
                    {
                        await streaming.CancelAsync().ConfigureAwait(false);
                        await rendering.ConfigureAwait(false);
                        streaming.Dispose();
                        call.Dispose();

                        streaming = CancellationTokenSource.CreateLinkedTokenSource(listening.Token);
                        listeningTo = context.UserSessionId;
                        call = client.Listen(
                            new ListenRequest { UserSessionId = listeningTo },
                            cancellationToken: streaming.Token);
                        _busy = false;
                    }

                    await Ready(output, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _busy = true;
                _ = await client.SendMessageAsync(
                    Message(context.UserSessionId, entered), cancellationToken: cancellationToken);

                if (rendering.IsCompleted)
                {
                    rendering = Render(call.ResponseStream, output, context.Error, streaming.Token);
                }
            }
        }
        finally
        {
            interrupts.Remove();
            _ = _interrupts.Writer.TryComplete();
            await listening.CancelAsync().ConfigureAwait(false);
            await rendering.ConfigureAwait(false);
            await interrupting.ConfigureAwait(false);
            streaming.Dispose();
            call.Dispose();
        }

        return CommandDispatcher.ExitSuccess;
    }

    private async Task<int> RawLoop(
        SlashContext context,
        TextWriter output,
        IRawTerminal terminal,
        CancellationToken cancellationToken)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var listeningTo = context.UserSessionId;
        var rendering = Task.CompletedTask;
        var interrupting = Interrupting(context, listening.Token);
        var decoder = new TerminalKeyDecoder();
        var editor = new IncrementalEditor(Prompt, 64 * 1024);
        var promptSync = new object();
        var currentPrompt = editor.Prompt;
        var renderer = new TerminalFrameRenderer(output, columns, new TerminalPalette(color()));
        var spinner = new TerminalSpinner(renderer);
        var buffer = new byte[4096];
        var exiting = false;
        var streaming = (CancellationTokenSource?)null;
        var call = (AsyncServerStreamingCall<Event>?)null;

        PromptValue CurrentPrompt()
        {
            lock (promptSync)
            {
                return currentPrompt;
            }
        }

        void UpdatePrompt()
        {
            lock (promptSync)
            {
                currentPrompt = editor.Prompt;
            }
        }

        interrupts.Install(this);

        try
        {
            streaming = CancellationTokenSource.CreateLinkedTokenSource(listening.Token);
            call = client.Listen(
                new ListenRequest { UserSessionId = listeningTo }, cancellationToken: streaming.Token);
            await DrawEditor(renderer, CurrentPrompt(), context, cancellationToken).ConfigureAwait(false);
            while (!cancellationToken.IsCancellationRequested && !exiting)
            {
                var count = await terminal.Read(buffer, cancellationToken).ConfigureAwait(false);
                var keys = count == 0 ? decoder.Flush() : decoder.Feed(buffer.AsSpan(0, count));
                foreach (var key in keys)
                {
                    if (key.Kind == TerminalKeyKind.Interrupt)
                    {
                        if (!editor.IsEmpty)
                        {
                            editor.Clear();
                            UpdatePrompt();
                        }
                        else
                        {
                            _ = Interrupted();
                        }
                    }
                    else if (key.Kind == TerminalKeyKind.EndOfFile && editor.IsEmpty)
                    {
                        exiting = true;
                        break;
                    }
                    else
                    {
                        var entered = editor.Apply(key);
                        UpdatePrompt();
                        if (entered is not null)
                        {
                            if (entered.Length == 0)
                            {
                                continue;
                            }

                            await renderer.Clear(cancellationToken).ConfigureAwait(false);
                            if (entered.StartsWith('/'))
                            {
                                if (await Dispatch(context, entered, cancellationToken).ConfigureAwait(false)
                                    == SlashOutcome.Exit)
                                {
                                    exiting = true;
                                    break;
                                }

                                if (!string.Equals(context.UserSessionId, listeningTo, StringComparison.Ordinal))
                                {
                                    await streaming.CancelAsync().ConfigureAwait(false);
                                    await rendering.ConfigureAwait(false);
                                    streaming.Dispose();
                                    streaming = null;
                                    call.Dispose();
                                    call = null;
                                    streaming = CancellationTokenSource.CreateLinkedTokenSource(listening.Token);
                                    listeningTo = context.UserSessionId;
                                    call = client.Listen(
                                        new ListenRequest { UserSessionId = listeningTo },
                                        cancellationToken: streaming.Token);
                                    _busy = false;
                                }
                            }
                            else
                            {
                                _busy = true;
                                await output.WriteLineAsync(
                                    $"› {TerminalText.Sanitize(entered)}".AsMemory(), cancellationToken)
                                    .ConfigureAwait(false);
                                _ = await client.SendMessageAsync(
                                    Message(context.UserSessionId, entered), cancellationToken: cancellationToken);
                                if (rendering.IsCompleted)
                                {
                                    var model = context.Model;
                                    rendering = spinner.Run(
                                        index => new TerminalFrame(
                                            [],
                                            new SpinnerValue("thinking", index),
                                            new ModelineValue("chat", "working", model),
                                            CurrentPrompt()),
                                        (stopSpinner, token) => RenderRaw(
                                            call.ResponseStream,
                                            output,
                                            context.Error,
                                            renderer,
                                            CurrentPrompt,
                                            context,
                                            stopSpinner,
                                            token),
                                        streaming.Token);
                                }
                            }
                        }
                    }

                    if (!_busy && !exiting)
                    {
                        await DrawEditor(renderer, CurrentPrompt(), context, cancellationToken).ConfigureAwait(false);
                    }
                }
            }
        }
        finally
        {
            try
            {
                interrupts.Remove();
                _ = _interrupts.Writer.TryComplete();
                await listening.CancelAsync().ConfigureAwait(false);
                try
                {
                    await rendering.ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await renderer.Clear(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        await interrupting.ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                streaming?.Dispose();
                call?.Dispose();
            }
        }

        return CommandDispatcher.ExitSuccess;
    }

    private async Task RenderRaw(
        IAsyncStreamReader<Event> stream,
        TextWriter output,
        TextWriter error,
        TerminalFrameRenderer renderer,
        Func<PromptValue> prompt,
        SlashContext context,
        Func<Task> stopSpinner,
        CancellationToken cancellationToken)
    {
        _ = await RenderTurn(
            stream,
            output,
            error,
            columns,
            cancellationToken,
            (_, _) => stopSpinner()).ConfigureAwait(false);

        _busy = false;
        _interruptRequested = false;
        if (!cancellationToken.IsCancellationRequested)
        {
            await DrawEditor(renderer, prompt(), context, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task Render(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        _ = await RenderTurn(stream, output, error, columns, cancellationToken).ConfigureAwait(false);
        _busy = false;
        _interruptRequested = false;
        await Ready(output, cancellationToken).ConfigureAwait(false);
    }

    private async Task Interrupting(SlashContext context, CancellationToken cancellationToken)
    {
        try
        {
            while (await _interrupts.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_interrupts.Reader.TryRead(out _))
                {
                    _ = await client.InterruptAsync(
                        new InterruptRequest { UserSessionId = context.UserSessionId },
                        cancellationToken: cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Ready(TextWriter output, CancellationToken cancellationToken)
    {
        if (_busy || cancellationToken.IsCancellationRequested)
        {
            return;
        }

        await output.WriteAsync(Prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<SlashOutcome> Dispatch(
        SlashContext context, string entered, CancellationToken cancellationToken)
    {
        var split = entered.IndexOf(' ', StringComparison.Ordinal);
        var name = split < 0 ? entered : entered[..split];
        var arguments = split < 0 ? string.Empty : entered[(split + 1)..].Trim();
        var command = commands.Find(name);

        if (command is null)
        {
            await context.Error
                .WriteLineAsync($"  unknown command {name}, try /help".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        return await command.Run(context, arguments, cancellationToken).ConfigureAwait(false);
    }

    private sealed class TurnView(TextWriter output, TextWriter error, Func<int> columns)
    {
        private const string Dim = "\u001b[2m";
        private const string Cyan = "\u001b[36m";
        private const string Green = "\u001b[32m";
        private const string Red = "\u001b[31m";
        private const string Reset = "\u001b[0m";

        private readonly LiveTerminalRenderer _live = new(output, columns);
        private bool _reasoning;
        private bool _reasoningEndsLine;
        private bool _started;
        private bool _textActive;
        private int _textSegment;

        private string TextId => $"assistant-{_textSegment}";

        public async Task<bool?> Render(Event published, CancellationToken cancellationToken)
        {
            if (_textActive && published.PayloadCase != Event.PayloadOneofCase.TextChunk)
            {
                await CommitText(cancellationToken).ConfigureAwait(false);
            }

            if (_reasoning && published.PayloadCase != Event.PayloadOneofCase.ReasoningChunk)
            {
                await EndReasoning(cancellationToken).ConfigureAwait(false);
            }

            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.TurnStarted:
                    _started = true;
                    break;

                case Event.PayloadOneofCase.InputAdmitted when _started:
                    await output.WriteLineAsync(
                        $"{Dim}  queued: {TerminalText.Sanitize(published.InputAdmitted.Content)}{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ReasoningChunk:
                    await RenderReasoning(published.ReasoningChunk.Fragment, cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TextChunk:
                    _textActive = true;
                    await _live.Append(
                        new LiveTerminalStreamMessage(TextId, string.Empty, published.TextChunk.Fragment),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolStarted:
                    await output.WriteLineAsync(
                        $"{Cyan}  * {TerminalText.Sanitize(published.ToolStarted.ToolName)} started{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolFinished:
                    await output.WriteLineAsync(
                        $"{Green}  + {TerminalText.Sanitize(published.ToolFinished.ToolName)} finished{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolCancelled:
                    await output.WriteLineAsync(
                        $"{Dim}  - {TerminalText.Sanitize(published.ToolCancelled.ToolName)} cancelled{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolError:
                    await output.WriteLineAsync(
                        ($"{Red}  ! {TerminalText.Sanitize(published.ToolError.ToolName)}: " +
                         $"{TerminalText.Sanitize(published.ToolError.Message)}{Reset}").AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentStarted:
                    await output.WriteLineAsync(
                        $"{Cyan}  * agent {TerminalText.Sanitize(published.AgentStarted.Name)} started{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentFinished:
                    await output.WriteLineAsync(
                        $"{Green}  + agent {TerminalText.Sanitize(published.AgentFinished.Name)} finished{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentFailed:
                    await output.WriteLineAsync(
                        ($"{Red}  ! agent {TerminalText.Sanitize(published.AgentFailed.Name)}: " +
                         $"{TerminalText.Sanitize(published.AgentFailed.Message)}{Reset}").AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TurnEnded:
                    await output.WriteLineAsync(
                        $"{Green}  {Summarise(published.TurnEnded)}{Reset}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    return true;

                case Event.PayloadOneofCase.TurnFailed:
                    await error.WriteLineAsync(
                        $"{Red}  {TerminalText.Sanitize(published.TurnFailed.Message)}{Reset}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    return false;

                default:
                    break;
            }

            return null;
        }

        public async Task End(CancellationToken cancellationToken)
        {
            if (_textActive)
            {
                await CommitText(cancellationToken).ConfigureAwait(false);
            }

            if (_reasoning)
            {
                await EndReasoning(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task Cancel(CancellationToken cancellationToken)
        {
            if (_textActive)
            {
                await _live.Clear(cancellationToken).ConfigureAwait(false);
            }

            if (_reasoning)
            {
                await output.WriteAsync(Reset.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
        }

        private static string Summarise(TurnEnded ended) =>
            $"{TerminalText.Sanitize(ended.FinishReason)} - {ended.InputTokens} in / {ended.OutputTokens} out";

        private async Task CommitText(CancellationToken cancellationToken)
        {
            await _live.Commit(cancellationToken).ConfigureAwait(false);
            _textActive = false;
            _textSegment++;
        }

        private async Task RenderReasoning(string fragment, CancellationToken cancellationToken)
        {
            if (!_reasoning)
            {
                await output.WriteAsync(Dim.AsMemory(), cancellationToken).ConfigureAwait(false);
                _reasoning = true;
            }

            var clean = TerminalText.Sanitize(fragment);
            await output.WriteAsync(clean.AsMemory(), cancellationToken).ConfigureAwait(false);
            _reasoningEndsLine = clean.EndsWith('\n');
        }

        private async Task EndReasoning(CancellationToken cancellationToken)
        {
            await output.WriteAsync(Reset.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (!_reasoningEndsLine)
            {
                await output.WriteLineAsync(ReadOnlyMemory<char>.Empty, cancellationToken).ConfigureAwait(false);
            }

            _reasoning = false;
            _reasoningEndsLine = false;
        }
    }
}

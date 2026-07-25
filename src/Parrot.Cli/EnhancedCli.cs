using System.Text;
using System.Threading.Channels;
using Grpc.Core;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class EnhancedCli(
    GeneratedParrot.ParrotClient client,
    SlashCommandRegistry commands,
    Interrupts interrupts,
    ICredentialStore credentials,
    OpenAiOAuthClient oauthClient,
    Configuration configuration,
    IReadOnlyList<string> providerIds,
    string model,
    string mode,
    string prompt,
    bool inputRedirected,
    TextReader input,
    TextWriter output,
    TextWriter error,
    Func<int> columns,
    Func<IRawTerminal?> rawTerminal,
    Func<bool> color) : IInterruptListener
{
    private const string DisableBracketedPaste = "\u001b[?2004l";
    private const string EnableBracketedPaste = "\u001b[?2004h";
    private const string Prompt = "> ";

    private readonly Channel<bool> _interrupts =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile bool _busy;
    private volatile bool _interruptRequested;

    public EnhancedCli(
        GeneratedParrot.ParrotClient client,
        SlashCommandRegistry commands,
        Interrupts interrupts,
        ICredentialStore credentials,
        OpenAiOAuthClient oauthClient,
        Configuration configuration,
        IReadOnlyList<string> providerIds,
        string model,
        string mode,
        string prompt,
        bool inputRedirected,
        TextReader input,
        TextWriter output,
        TextWriter error)
        : this(
            client,
            commands,
            interrupts,
            credentials,
            oauthClient,
            configuration,
            providerIds,
            model,
            mode,
            prompt,
            inputRedirected,
            input,
            output,
            error,
            static () => Console.WindowWidth,
            static () => string.Equals(
                Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.Ordinal)
                ? null
                : UnixRawTerminal.Open(),
            static () => Environment.GetEnvironmentVariable("NO_COLOR") is null)
    {
    }

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var text = prompt;

        if (text.Length == 0 && inputRedirected)
        {
            text = (await input.ReadToEndAsync(cancellationToken).ConfigureAwait(false)).Trim();

            if (text.Length == 0)
            {
                return CommandDispatcher.ExitUsage;
            }
        }

        UserSession session;

        try
        {
            session = await client.CreateSessionAsync(
                new CreateSessionRequest { Model = model, Mode = mode }, cancellationToken: cancellationToken);
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
            providerIds,
            session.Id,
            session.Model,
            session.Mode,
            input,
            output,
            error);

        return text.Length > 0
            ? await Once(context, text, output, cancellationToken).ConfigureAwait(false)
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
        Func<Event, CancellationToken, Task>? beforeRender = null,
        bool renderActivityEvents = true,
        Func<Event, CancellationToken, Task>? afterRender = null,
        bool color = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        var view = new TurnView(output, error, columns, renderActivityEvents, color);

        try
        {
            while (await stream.MoveNext(cancellationToken).ConfigureAwait(false))
            {
                if (beforeRender is { } before)
                {
                    await before(stream.Current, cancellationToken).ConfigureAwait(false);
                }

                await view.Prepare(stream.Current, cancellationToken).ConfigureAwait(false);
                var completed = await view.Render(stream.Current, cancellationToken).ConfigureAwait(false);
                if (afterRender is { } after)
                {
                    await after(stream.Current, cancellationToken).ConfigureAwait(false);
                }

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

    internal static async Task SetBracketedPaste(
        TextWriter output, bool enabled, CancellationToken cancellationToken)
    {
        var sequence = enabled ? EnableBracketedPaste : DisableBracketedPaste;
        await output.WriteAsync(sequence.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

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
            new TerminalFrame([], null, new ModelineValue(context.Mode, "ready", context.Model), prompt),
            cancellationToken);

    private static string Activity(Event published, bool started) => published.PayloadCase switch
    {
        Event.PayloadOneofCase.None => $"event {TerminalText.Sanitize(published.Id)} has no payload",
        Event.PayloadOneofCase.TurnStarted =>
            $"turn started: {TerminalText.Sanitize(published.TurnStarted.Model)}",
        Event.PayloadOneofCase.InputAdmitted =>
            $"{(started ? "queued" : "input admitted")}: {TerminalText.Sanitize(published.InputAdmitted.Content)}",
        Event.PayloadOneofCase.InputPromoted =>
            $"input promoted: {TerminalText.Sanitize(published.InputPromoted.InputId)}",
        Event.PayloadOneofCase.ToolCallChunk =>
            $"tool call {TerminalText.Sanitize(published.ToolCallChunk.ToolName)}: " +
            TerminalText.Sanitize(published.ToolCallChunk.ArgumentsFragment),
        Event.PayloadOneofCase.RetryNotice =>
            $"retry {published.RetryNotice.Attempt} in {published.RetryNotice.RetryAfterMs} ms: " +
            TerminalText.Sanitize(published.RetryNotice.Reason),
        Event.PayloadOneofCase.ToolStarted =>
            $"* {TerminalText.Sanitize(published.ToolStarted.ToolName)} started",
        Event.PayloadOneofCase.ToolFinished =>
            $"+ {TerminalText.Sanitize(published.ToolFinished.ToolName)} finished",
        Event.PayloadOneofCase.ToolCancelled =>
            $"- {TerminalText.Sanitize(published.ToolCancelled.ToolName)} cancelled",
        Event.PayloadOneofCase.ToolError =>
            $"! {TerminalText.Sanitize(published.ToolError.ToolName)}: " +
            TerminalText.Sanitize(published.ToolError.Message),
        Event.PayloadOneofCase.AgentStarted =>
            $"* agent {TerminalText.Sanitize(published.AgentStarted.Name)} started",
        Event.PayloadOneofCase.AgentFinished =>
            $"+ agent {TerminalText.Sanitize(published.AgentFinished.Name)} finished",
        Event.PayloadOneofCase.AgentFailed =>
            $"! agent {TerminalText.Sanitize(published.AgentFailed.Name)}: " +
            TerminalText.Sanitize(published.AgentFailed.Message),
        _ => string.Empty,
    };

    private async Task<int> Once(
        SlashContext context, string prompt, TextWriter output, CancellationToken cancellationToken)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var call = client.Listen(
            new ListenRequest { UserSessionId = context.UserSessionId }, cancellationToken: listening.Token);

        _ = await client.SendMessageAsync(
            Message(context.UserSessionId, prompt), cancellationToken: cancellationToken);

        var completed = await RenderTurn(
            call.ResponseStream,
            output,
            context.Error,
            columns,
            listening.Token,
            color: color())
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
            await SetBracketedPaste(output, true, cancellationToken).ConfigureAwait(false);
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
                                await renderer.CommitUserMessage("› ", entered, cancellationToken)
                                    .ConfigureAwait(false);
                                _ = await client.SendMessageAsync(
                                    Message(context.UserSessionId, entered), cancellationToken: cancellationToken);
                                if (rendering.IsCompleted)
                                {
                                    var model = context.Model;
                                    var mode = context.Mode;
                                    rendering = spinner.Run(
                                        index => new TerminalFrame(
                                            [],
                                            new SpinnerValue("thinking", index),
                                            new ModelineValue(mode, "working", model),
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

                    if (!exiting)
                    {
                        if (_busy)
                        {
                            await renderer.UpdatePrompt(CurrentPrompt(), cancellationToken).ConfigureAwait(false);
                        }
                        else
                        {
                            await DrawEditor(renderer, CurrentPrompt(), context, cancellationToken)
                                .ConfigureAwait(false);
                        }
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
                await SetBracketedPaste(output, false, CancellationToken.None).ConfigureAwait(false);
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
        var spinning = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            using var activity = new RawActivityView(
                renderer,
                prompt,
                () => new ModelineValue(context.Mode, "working", context.Model));
            using var animating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var animation = activity.Run(animating.Token);

            async Task BeforeRender(Event published, CancellationToken token)
            {
                if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted)
                {
                    _busy = true;
                }

                if (spinning)
                {
                    spinning = false;
                    await stopSpinner().ConfigureAwait(false);
                }

                await activity.Prepare(published, token).ConfigureAwait(false);
            }

            bool completed;
            try
            {
                completed = await RenderTurn(
                    stream,
                    output,
                    error,
                    columns,
                    cancellationToken,
                    BeforeRender,
                    false,
                    activity.Render,
                    color()).ConfigureAwait(false);
            }
            finally
            {
                await animating.CancelAsync().ConfigureAwait(false);
                await animation.ConfigureAwait(false);
            }

            if (!completed)
            {
                return;
            }

            _busy = false;
            _interruptRequested = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                await DrawEditor(renderer, prompt(), context, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task Render(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var completed = await RenderTurn(
                stream,
                output,
                error,
                columns,
                cancellationToken,
                (published, _) =>
                {
                    if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted)
                    {
                        _busy = true;
                    }

                    return Task.CompletedTask;
                },
                color: color()).ConfigureAwait(false);
            if (!completed)
            {
                return;
            }

            _busy = false;
            _interruptRequested = false;
            await Ready(output, cancellationToken).ConfigureAwait(false);
        }
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

    internal sealed class RawActivityView(
        TerminalFrameRenderer renderer,
        Func<PromptValue> prompt,
        Func<ModelineValue> modeline) : IDisposable
    {
        private const int SpinnerIntervalMilliseconds = 80;

        private readonly StringBuilder _reasoning = new();
        private readonly Dictionary<string, (string Name, StringBuilder Arguments)> _toolCalls = [];
        private readonly SemaphoreSlim _rendering = new(1, 1);
        private IReadOnlyList<string> _rows = [];
        private string? _activeToolCallId;
        private bool _started;

        public void Dispose() => _rendering.Dispose();

        public async Task Run(CancellationToken cancellationToken)
        {
            try
            {
                for (var frame = 0; ; frame++)
                {
                    await Task.Delay(SpinnerIntervalMilliseconds, cancellationToken).ConfigureAwait(false);
                    await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
                    try
                    {
                        if (_activeToolCallId is not null
                            && _toolCalls.TryGetValue(_activeToolCallId, out var toolCall))
                        {
                            await renderer.Draw(
                                new TerminalFrame(
                                    [],
                                    new SpinnerValue(FormatToolCall(toolCall), frame),
                                    modeline(),
                                    prompt()),
                                cancellationToken).ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        _ = _rendering.Release();
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
        }

        public async Task Prepare(Event published, CancellationToken cancellationToken)
        {
            if (published.PayloadCase is not (Event.PayloadOneofCase.TextChunk or
                Event.PayloadOneofCase.TurnEnded or
                Event.PayloadOneofCase.TurnFailed))
            {
                return;
            }

            await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await Flush(false, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = _rendering.Release();
            }
        }

        public async Task Render(Event published, CancellationToken cancellationToken)
        {
            await _rendering.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _started |= published.PayloadCase == Event.PayloadOneofCase.TurnStarted;
                if (published.PayloadCase is
                    Event.PayloadOneofCase.TextChunk or
                    Event.PayloadOneofCase.TurnEnded or
                    Event.PayloadOneofCase.TurnFailed)
                {
                    return;
                }

                var activity = published.PayloadCase switch
                {
                    Event.PayloadOneofCase.ReasoningChunk => Reasoning(published.ReasoningChunk.Fragment),
                    Event.PayloadOneofCase.ToolCallChunk => ToolCall(published.ToolCallChunk),
                    _ => Activity(published, _started),
                };
                _rows = Rows(published, activity);
                if (IsTerminalToolEvent(published))
                {
                    await Flush(true, cancellationToken).ConfigureAwait(false);
                    RemoveToolCall(published);
                    return;
                }

                SpinnerValue? spinner = published.PayloadCase == Event.PayloadOneofCase.ToolCallChunk
                    ? new SpinnerValue(activity, 0)
                    : null;
                if (spinner is not null)
                {
                    _rows = [];
                }

                await renderer.Draw(
                    new TerminalFrame(
                        _rows,
                        spinner,
                        modeline(),
                        prompt()),
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _ = _rendering.Release();
            }
        }

        private static string FormatToolCall((string Name, StringBuilder Arguments) toolCall) =>
            $"tool call {TerminalText.Sanitize(toolCall.Name)}: {TerminalText.Sanitize(toolCall.Arguments.ToString())}";

        private static bool IsTerminalToolEvent(Event published) =>
            published.PayloadCase is Event.PayloadOneofCase.ToolFinished or
                Event.PayloadOneofCase.ToolCancelled or
                Event.PayloadOneofCase.ToolError;

        private static string? TerminalToolCallId(Event published) => published.PayloadCase switch
        {
            Event.PayloadOneofCase.ToolFinished => published.ToolFinished.ToolCallId,
            Event.PayloadOneofCase.ToolCancelled => published.ToolCancelled.ToolCallId,
            Event.PayloadOneofCase.ToolError => published.ToolError.ToolCallId,
            _ => null,
        };

        private string Reasoning(string fragment)
        {
            _ = _reasoning.Append(TerminalText.Sanitize(fragment));
            return _reasoning.ToString();
        }

        private IReadOnlyList<string> Rows(Event published, string activity)
        {
            var toolCallId = TerminalToolCallId(published);
            if (toolCallId is not null && _toolCalls.TryGetValue(toolCallId, out var toolCall))
            {
                return published.PayloadCase switch
                {
                    Event.PayloadOneofCase.ToolFinished => [$"+ {FormatToolCall(toolCall)}"],
                    Event.PayloadOneofCase.ToolCancelled => [$"- {FormatToolCall(toolCall)} cancelled"],
                    Event.PayloadOneofCase.ToolError =>
                        [$"! {FormatToolCall(toolCall)}: {TerminalText.Sanitize(published.ToolError.Message)}"],
                    _ => [FormatToolCall(toolCall)],
                };
            }

            return activity.Length == 0 ? [] : [activity];
        }

        private string ToolCall(ToolCallChunk chunk)
        {
            if (!_toolCalls.TryGetValue(chunk.ToolCallId, out var toolCall))
            {
                toolCall = (chunk.ToolName, new StringBuilder());
            }
            else if (chunk.ToolName.Length > 0)
            {
                toolCall.Name = chunk.ToolName;
            }

            _ = toolCall.Arguments.Append(chunk.ArgumentsFragment);
            _toolCalls[chunk.ToolCallId] = toolCall;
            _activeToolCallId = chunk.ToolCallId;
            return FormatToolCall(toolCall);
        }

        private void RemoveToolCall(Event published)
        {
            var toolCallId = TerminalToolCallId(published);
            if (toolCallId is null)
            {
                return;
            }

            _ = _toolCalls.Remove(toolCallId);
            if (string.Equals(_activeToolCallId, toolCallId, StringComparison.Ordinal))
            {
                _activeToolCallId = null;
            }
        }

        private async Task Flush(bool redraw, CancellationToken cancellationToken)
        {
            var activities = _rows;
            _rows = [];
            if (redraw)
            {
                await renderer.FlushActivitiesAndDraw(
                    activities,
                    new TerminalFrame(
                        _rows,
                        null,
                        modeline(),
                        prompt()),
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            await renderer.FlushActivities(activities, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class TurnView(
        TextWriter output,
        TextWriter error,
        Func<int> columns,
        bool renderActivityEvents,
        bool color)
    {
        private const string Dim = "\u001b[2m";
        private const string Cyan = "\u001b[36m";
        private const string Green = "\u001b[32m";
        private const string Red = "\u001b[31m";
        private const string Reset = "\u001b[0m";

        private readonly MarkdownLiveRenderer _live = new(output, columns, color);
        private bool _reasoning;
        private bool _reasoningEndsLine;
        private bool _started;
        private bool _textActive;
        private int _textSegment;

        private string TextId => $"assistant-{_textSegment}";

        public async Task Prepare(Event published, CancellationToken cancellationToken)
        {
            if (_textActive && published.PayloadCase != Event.PayloadOneofCase.TextChunk)
            {
                await CommitText(cancellationToken).ConfigureAwait(false);
            }

            if (_reasoning && published.PayloadCase != Event.PayloadOneofCase.ReasoningChunk)
            {
                await EndReasoning(cancellationToken).ConfigureAwait(false);
            }
        }

        public async Task<bool?> Render(Event published, CancellationToken cancellationToken)
        {
            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.TurnStarted:
                    _started = true;
                    if (renderActivityEvents)
                    {
                        await RenderActivity(published, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case Event.PayloadOneofCase.None:
                case Event.PayloadOneofCase.InputAdmitted:
                case Event.PayloadOneofCase.InputPromoted:
                case Event.PayloadOneofCase.ToolCallChunk:
                case Event.PayloadOneofCase.RetryNotice:
                case Event.PayloadOneofCase.ToolStarted:
                case Event.PayloadOneofCase.ToolFinished:
                case Event.PayloadOneofCase.ToolCancelled:
                case Event.PayloadOneofCase.ToolError:
                case Event.PayloadOneofCase.AgentStarted:
                case Event.PayloadOneofCase.AgentFinished:
                case Event.PayloadOneofCase.AgentFailed:
                    if (renderActivityEvents)
                    {
                        await RenderActivity(published, cancellationToken).ConfigureAwait(false);
                    }

                    break;

                case Event.PayloadOneofCase.StatusInjected:
                    await output.WriteLineAsync(
                        $"{Dim}  ↻ Status prompt injected{Reset}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ReasoningChunk:
                    if (renderActivityEvents)
                    {
                        await RenderReasoning(published.ReasoningChunk.Fragment, cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                case Event.PayloadOneofCase.TextChunk:
                    _textActive = true;
                    await _live.Append(
                        new LiveTerminalStreamMessage(TextId, string.Empty, published.TextChunk.Fragment),
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

        private Task RenderActivity(Event published, CancellationToken cancellationToken)
        {
            var style = published.PayloadCase switch
            {
                Event.PayloadOneofCase.ToolStarted or
                Event.PayloadOneofCase.AgentStarted => Cyan,
                Event.PayloadOneofCase.ToolFinished or
                Event.PayloadOneofCase.AgentFinished => Green,
                Event.PayloadOneofCase.ToolError or
                Event.PayloadOneofCase.AgentFailed => Red,
                _ => Dim,
            };
            return output.WriteLineAsync(
                $"{style}  {Activity(published, _started)}{Reset}".AsMemory(),
                cancellationToken);
        }

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

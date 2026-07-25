using System.Text;
using System.Threading.Channels;
using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedCli(
    GeneratedParrot.ParrotClient client,
    SlashCommandRegistry commands,
    Interrupts interrupts,
    EnhancedChatRequest request,
    EnhancedSlashContextFactory contexts,
    ITerminal terminal) : IInterruptListener
{
    private const string DisableBracketedPaste = "\u001b[?2004l";
    private const string DisableKeyboardEnhancement = "\u001b[<u";
    private const string EnableBracketedPaste = "\u001b[?2004h";
    private const string EnableKeyboardEnhancement = "\u001b[>1u";
    private const string Prompt = "> ";

    private readonly Channel<bool> _interrupts =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile bool _busy;
    private volatile bool _interruptRequested;

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var text = request.Prompt;

        UserSession session;

        try
        {
            session = await client.CreateSessionAsync(request.Session, cancellationToken: cancellationToken);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
        {
            await terminal.Error.WriteLineAsync($"parrot: {failure.Status.Detail}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return CommandDispatcher.ExitFailure;
        }

        var context = contexts.Create(session);

        return await Loop(context, text, terminal.Output, text.Length > 0, cancellationToken).ConfigureAwait(false);
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

    internal static async Task SetBracketedPaste(
        TextWriter output, bool enabled, CancellationToken cancellationToken)
    {
        var sequence = enabled ? EnableBracketedPaste : DisableBracketedPaste;
        await output.WriteAsync(sequence.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal static async Task SetKeyboardEnhancement(
        TextWriter output, bool enabled, CancellationToken cancellationToken)
    {
        var sequence = enabled ? EnableKeyboardEnhancement : DisableKeyboardEnhancement;
        await output.WriteAsync(sequence.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        CancellationToken cancellationToken,
        Func<Event, CancellationToken, Task>? beforeRender = null,
        bool renderActivityEvents = true,
        Func<Event, CancellationToken, Task>? afterRender = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var view = new TurnView(
            terminal.Output,
            terminal.Error,
            terminal.GetColumns,
            renderActivityEvents,
            terminal.Color);

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

    private async Task<int> Loop(
        SlashContext context,
        string initialPrompt,
        TextWriter output,
        bool exitOnFirstCompletion,
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
        var renderer = new TerminalFrameRenderer(output, terminal.GetColumns, new TerminalPalette(terminal.Color));
        var spinner = new TerminalSpinner(renderer);
        var buffer = new byte[4096];
        var exiting = false;
        var firstTurnCompleted = false;
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

        async Task StartTurn(string entered, AsyncServerStreamingCall<Event> activeCall)
        {
            _busy = true;
            await renderer.CommitUserMessage("› ", entered, cancellationToken).ConfigureAwait(false);
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
                    async (stopSpinner, token) => firstTurnCompleted = await RenderRaw(
                        activeCall.ResponseStream,
                        renderer,
                        CurrentPrompt,
                        context,
                        stopSpinner,
                        exitOnFirstCompletion,
                        token).ConfigureAwait(false),
                    streaming?.Token ?? cancellationToken);
            }
        }

        interrupts.Install(this);

        try
        {
            await SetBracketedPaste(output, true, cancellationToken).ConfigureAwait(false);
            await SetKeyboardEnhancement(output, true, cancellationToken).ConfigureAwait(false);
            streaming = CancellationTokenSource.CreateLinkedTokenSource(listening.Token);
            call = client.Listen(
                new ListenRequest { UserSessionId = listeningTo }, cancellationToken: streaming.Token);
            await DrawEditor(renderer, CurrentPrompt(), context, cancellationToken).ConfigureAwait(false);
            if (initialPrompt.Length > 0)
            {
                await StartTurn(initialPrompt, call).ConfigureAwait(false);
                if (exitOnFirstCompletion)
                {
                    await rendering.ConfigureAwait(false);
                    exiting = true;
                }
            }

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
                        await renderer.UpdatePrompt(CurrentPrompt(), cancellationToken).ConfigureAwait(false);
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
                                await StartTurn(entered, call).ConfigureAwait(false);
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
                await SetKeyboardEnhancement(output, false, CancellationToken.None).ConfigureAwait(false);
                await SetBracketedPaste(output, false, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return exitOnFirstCompletion && !firstTurnCompleted
            ? CommandDispatcher.ExitFailure
            : CommandDispatcher.ExitSuccess;
    }

    private async Task<bool> RenderRaw(
        IAsyncStreamReader<Event> stream,
        TerminalFrameRenderer renderer,
        Func<PromptValue> prompt,
        SlashContext context,
        Func<Task> stopSpinner,
        bool exitOnFirstCompletion,
        CancellationToken cancellationToken)
    {
        var spinning = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            var failed = false;
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
                else if (published.PayloadCase == Event.PayloadOneofCase.TurnFailed)
                {
                    failed = true;
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
                    cancellationToken,
                    BeforeRender,
                    false,
                    activity.Render).ConfigureAwait(false);
            }
            finally
            {
                await animating.CancelAsync().ConfigureAwait(false);
                await animation.ConfigureAwait(false);
            }

            if ((!completed && !failed) || exitOnFirstCompletion)
            {
                return completed;
            }

            _busy = false;
            _interruptRequested = false;
            if (!cancellationToken.IsCancellationRequested)
            {
                await DrawEditor(renderer, prompt(), context, cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
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

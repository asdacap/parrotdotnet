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
        Func<Event, CancellationToken, Task>? afterRender = null,
        TerminalFrameRenderer? renderer = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var turnRenderer = renderer ?? new TerminalFrameRenderer(
            terminal.Output,
            terminal.GetColumns,
            new TerminalPalette(terminal.Color),
            TerminalFrameRenderer.DefaultLiveRows,
            TerminalFrameRenderer.DefaultInputRows);
        var view = new EnhancedTurnView(
            turnRenderer,
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
        var renderer = new TerminalFrameRenderer(
            output,
            terminal.GetColumns,
            new TerminalPalette(terminal.Color),
            TerminalFrameRenderer.DefaultLiveRows,
            TerminalFrameRenderer.DefaultInputRows);
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
            await renderer.Draw(
                new TerminalFrame(
                    [],
                    null,
                    new ModelineValue(context.Mode, "ready", context.Model),
                    CurrentPrompt()),
                cancellationToken).ConfigureAwait(false);
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
                        await renderer.Draw(
                            new TerminalFrame(
                                [],
                                null,
                                new ModelineValue(context.Mode, "ready", context.Model),
                                CurrentPrompt()),
                            cancellationToken).ConfigureAwait(false);
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
                    activity.Render,
                    renderer).ConfigureAwait(false);
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
                await renderer.Draw(
                    new TerminalFrame(
                        [],
                        null,
                        new ModelineValue(context.Mode, "ready", context.Model),
                        prompt()),
                    cancellationToken).ConfigureAwait(false);
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
}

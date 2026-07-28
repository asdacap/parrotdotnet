using System.Threading.Channels;
using Grpc.Core;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedCli(
    GeneratedParrot.ParrotClient client,
    SlashCommandRegistry commands,
    Interrupts interrupts,
    EnhancedChatRequest request,
    ICredentialStore credentials,
    OpenAiOAuthClient oauthClient,
    Configuration configuration,
    IReadOnlyList<string> providerIds,
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

        var context = SlashContext.Create(
            client,
            credentials,
            oauthClient,
            configuration,
            providerIds,
            session,
            new TerminalPromptReader(terminal),
            terminal.Output,
            terminal.Error);

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
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task>? draw = null,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task>? commit = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        async Task CommitStandalone(
            IScrollbackItem scrollback,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var context = new ScrollbackRenderContext(
                Math.Max(1, terminal.GetColumns()),
                new TerminalPalette(terminal.Color));
            foreach (var line in scrollback.Render(context))
            {
                await terminal.Output.WriteAsync(line.AsMemory(), CancellationToken.None).ConfigureAwait(false);
                await terminal.Output.WriteAsync("\r\n".AsMemory(), CancellationToken.None).ConfigureAwait(false);
            }

            await terminal.Output.FlushAsync(CancellationToken.None).ConfigureAwait(false);
        }

        var view = new EnhancedTurnView(
            draw ?? NoLiveDraw,
            commit ?? CommitStandalone,
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
        catch
        {
            await view.Cancel(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static Task NoLiveDraw(IReadOnlyList<ILiveBufferItem> items, CancellationToken cancellationToken) =>
        Task.CompletedTask;

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
        var currentPrompt = editor.Prompt;
        var currentBody = (IReadOnlyList<ILiveBufferItem>)[];
        var currentModeline = new ModelineValue(context.Mode, "ready", context.Model);
        using var composing = new SemaphoreSlim(1, 1);
        var palette = new TerminalPalette(terminal.Color);
        var renderer = new TerminalFrameRenderer(
            output,
            terminal.GetColumns,
            palette,
            TerminalFrameRenderer.DefaultLiveRows,
            TerminalFrameRenderer.DefaultInputRows);
        var spinner = new TerminalSpinner(DrawBody);
        var buffer = new byte[4096];
        var exiting = false;
        var firstTurnCompleted = false;
        var streaming = (CancellationTokenSource?)null;
        var call = (AsyncServerStreamingCall<Event>?)null;

        IReadOnlyList<ILiveBufferItem> Snapshot() => [.. currentBody, currentModeline, currentPrompt];

        async Task DrawBody(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                currentBody = [.. items];
                await renderer.Draw(Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task CommitBody(
            IScrollbackItem scrollback,
            IReadOnlyList<ILiveBufferItem> items,
            CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                currentBody = [.. items];
                await renderer.Commit(scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task DrawPrompt(CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                currentPrompt = editor.Prompt;
                await renderer.Draw(Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task DrawState(ModelineValue modeline, CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                currentBody = [];
                currentModeline = modeline;
                currentPrompt = editor.Prompt;
                await renderer.Draw(Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task Clear(CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await renderer.Clear(CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task StartTurn(string entered, AsyncServerStreamingCall<Event> activeCall)
        {
            _busy = true;
            var committed = ImmediateScrollbackValue.User("› " + entered);
            await composing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                currentBody = [];
                currentModeline = new ModelineValue(context.Mode, "working", context.Model);
                currentPrompt = editor.Prompt;
                await renderer.Commit(committed, Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }

            _ = await client.SendMessageAsync(
                Message(context.UserSessionId, entered), cancellationToken: cancellationToken);
            if (rendering.IsCompleted)
            {
                rendering = spinner.Run(
                    index => new SpinnerValue("thinking", index),
                    async (stopSpinner, token) => firstTurnCompleted = await RenderRaw(
                        activeCall.ResponseStream,
                        DrawBody,
                        CommitBody,
                        token => DrawState(new ModelineValue(context.Mode, "ready", context.Model), token),
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
            await DrawState(
                new ModelineValue(context.Mode, "ready", context.Model),
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
                            await DrawPrompt(cancellationToken).ConfigureAwait(false);
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
                        await DrawPrompt(cancellationToken).ConfigureAwait(false);
                        if (entered is not null)
                        {
                            if (entered.Length == 0)
                            {
                                continue;
                            }

                            await Clear(cancellationToken).ConfigureAwait(false);
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
                        await DrawState(
                            new ModelineValue(context.Mode, "ready", context.Model),
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
                        await Clear(CancellationToken.None).ConfigureAwait(false);
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
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        Func<CancellationToken, Task> ready,
        Func<Task> stopSpinner,
        bool exitOnFirstCompletion,
        CancellationToken cancellationToken)
    {
        var spinning = true;

        while (!cancellationToken.IsCancellationRequested)
        {
            var failed = false;
            using var activity = new RawActivityView(draw, commit);
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
                    draw,
                    commit).ConfigureAwait(false);
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
                await ready(cancellationToken).ConfigureAwait(false);
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

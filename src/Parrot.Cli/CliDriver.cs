using System.Threading.Channels;
using Grpc.Core;
using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

// Drives a client, local or remote -- they are the same contract, so this does
// not know which it holds. One prompt is answered once; otherwise it opens a
// session and loops, reusing a single Listen call turn after turn, which is
// what the stream being indefinite was for.
//
// Reading and rendering run at the same time, because a prompt is admitted
// whether or not a turn is running: waiting for the turn to end before reading
// the next line would make the queue unreachable from the one client most
// likely to want it. They never write at the same time, because the only thing
// the reading half prints is the prompt, and it prints that only when nothing
// is streaming.
//
// Rendering is the one thing it does not do: that is ITurnRenderer, so the two
// CLIs stay separate.
internal sealed class CliDriver(
    GeneratedParrot.ParrotClient client,
    ITurnRenderer renderer,
    SlashCommandRegistry commands,
    Interrupts interrupts) : IInterruptListener
{
    private const string Prompt = "> ";

    // One at a time: a second Ctrl-C arriving before the first has stopped the
    // turn is the user saying they meant the process, not the turn.
    private readonly Channel<bool> _interrupts =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    // Written by the reading half and the rendering half; read by the signal
    // handler. Only ever a hint about what the session was doing a moment ago,
    // which is all the two decisions it feeds need.
    private volatile bool _busy;
    private volatile bool _interruptRequested;

    // A prompt on the command line, or piped stdin, means the caller wants one
    // answer and not a session. Scripts and CI depend on that.
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

    // Taken only while a turn is running. Idle, Ctrl-C means what it has always
    // meant, and so does a second one: the request is still outstanding, so
    // asking again is asking for something else.
    public bool Interrupted()
    {
        if (!_busy || _interruptRequested)
        {
            return false;
        }

        _interruptRequested = true;

        // Handed to a reader rather than sent here: this runs on the signal
        // handler's thread, which must not wait on an RPC.
        return _interrupts.Writer.TryWrite(true);
    }

    private static SendMessageRequest Message(string userSessionId, string text) =>
        new()
        {
            UserSessionId = userSessionId,
            Text = text,

            // Steer, always. A prompt typed at a session is meant for the work
            // in front of it, and queueing is for clients that speak for
            // somebody who is not watching.
            Delivery = Delivery.Steer,
        };

    private async Task<int> Once(
        SlashContext context, string prompt, TextWriter output, CancellationToken cancellationToken)
    {
        // The stream is indefinite, so this cancels once its one turn is done
        // rather than waiting for the server to stop.
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // Listen before sending: a stream opened after the turn starts would
        // miss its opening events.
        using var call = client.Listen(
            new ListenRequest { UserSessionId = context.UserSessionId }, cancellationToken: listening.Token);

        _ = await client.SendMessageAsync(
            Message(context.UserSessionId, prompt), cancellationToken: cancellationToken);

        var completed = await renderer
            .RenderTurn(call.ResponseStream, output, context.Error, listening.Token)
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

        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        // A second one, per stream: /clear moves the session, and ending the
        // stream it moved off must not end the loop that moved it.
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
                    if (await Dispatch(context, entered, cancellationToken).ConfigureAwait(false)
                        == SlashOutcome.Exit)
                    {
                        break;
                    }

                    // /clear moves the session, so the stream has to follow it
                    // -- and whatever is rendering the old one has to stop
                    // before the reader it holds is disposed underneath it.
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

                // A turn is already being rendered, and a steer joins it rather
                // than starting one, so there is nothing new to render.
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

            // Neither half is abandoned: the loop does not return while work it
            // started is still in flight.
            await rendering.ConfigureAwait(false);
            await interrupting.ConfigureAwait(false);
            streaming.Dispose();
            call.Dispose();
        }

        return CommandDispatcher.ExitSuccess;
    }

    // One turn, then the session is idle again and says so. Started rather than
    // awaited, so the next line can be typed while this runs.
    private async Task Render(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        _ = await renderer.RenderTurn(stream, output, error, cancellationToken).ConfigureAwait(false);

        _busy = false;
        _interruptRequested = false;

        await Ready(output, cancellationToken).ConfigureAwait(false);
    }

    // Reads the signals the handler could not act on itself.
    private async Task Interrupting(SlashContext context, CancellationToken cancellationToken)
    {
        try
        {
            // One at a time: the channel holds one, and the listener refuses a
            // second signal until this one has been answered.
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
            // The loop is leaving, which is the only thing that cancels this.
        }
    }

    // Silent while a turn is streaming: a prompt printed into the middle of an
    // answer is a lie about whose turn it is, and it is the one thing the
    // reading half would otherwise write over the top of the rendering half.
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
            // Never sent to the model: a mistyped command is a mistake, not a
            // prompt, and silently spending tokens on it would be worse.
            await context.Error
                .WriteLineAsync($"  unknown command {name}, try /help".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return SlashOutcome.Continue;
        }

        return await command.Run(context, arguments, cancellationToken).ConfigureAwait(false);
    }
}

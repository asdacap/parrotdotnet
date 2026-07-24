using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

// Drives a client, local or remote -- they are the same contract, so this does
// not know which it holds. One prompt is answered once; otherwise it opens a
// session and loops, reusing a single Listen call turn after turn, which is
// what the stream being indefinite was for.
//
// Rendering is the one thing it does not do: that is ITurnRenderer, so the two
// CLIs stay separate.
internal sealed class CliDriver(
    GeneratedParrot.ParrotClient client,
    ITurnRenderer renderer,
    SlashCommandRegistry commands)
{
    private const string Prompt = "> ";

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
            new SendMessageRequest { UserSessionId = context.UserSessionId, Text = prompt },
            cancellationToken: cancellationToken);

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
        var listeningTo = context.UserSessionId;
        var call = client.Listen(
            new ListenRequest { UserSessionId = listeningTo }, cancellationToken: listening.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await output.WriteAsync(Prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);

                var line = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);

                if (line is null)
                {
                    break;
                }

                var entered = line.Trim();

                if (entered.Length == 0)
                {
                    continue;
                }

                if (entered.StartsWith('/'))
                {
                    if (await Dispatch(context, entered, cancellationToken).ConfigureAwait(false)
                        == SlashOutcome.Exit)
                    {
                        break;
                    }

                    // /clear moves the session, so the stream has to follow it.
                    if (!string.Equals(context.UserSessionId, listeningTo, StringComparison.Ordinal))
                    {
                        call.Dispose();
                        listeningTo = context.UserSessionId;
                        call = client.Listen(
                            new ListenRequest { UserSessionId = listeningTo },
                            cancellationToken: listening.Token);
                    }

                    continue;
                }

                _ = await client.SendMessageAsync(
                    new SendMessageRequest { UserSessionId = context.UserSessionId, Text = entered },
                    cancellationToken: cancellationToken);

                _ = await renderer
                    .RenderTurn(call.ResponseStream, output, context.Error, listening.Token)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            call.Dispose();
            await listening.CancelAsync().ConfigureAwait(false);
        }

        return CommandDispatcher.ExitSuccess;
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

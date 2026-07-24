using Parrot.Cli.Commands;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

// The REPL. One Listen call for the whole session, reused turn after turn --
// which is what the stream being indefinite was for.
internal static class InteractiveSession
{
    private const string Prompt = "> ";

    public static async Task<int> Run(
        GeneratedParrot.ParrotClient client,
        ITurnRenderer renderer,
        SlashCommandRegistry registry,
        SlashContext context,
        TextReader input,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(renderer);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(context);

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
                    if (await Dispatch(registry, context, entered, cancellationToken).ConfigureAwait(false)
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

    private static async Task<SlashOutcome> Dispatch(
        SlashCommandRegistry registry,
        SlashContext context,
        string entered,
        CancellationToken cancellationToken)
    {
        var split = entered.IndexOf(' ', StringComparison.Ordinal);
        var name = split < 0 ? entered : entered[..split];
        var arguments = split < 0 ? string.Empty : entered[(split + 1)..].Trim();

        var command = registry.Find(name);

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

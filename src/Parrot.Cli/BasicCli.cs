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

internal sealed class BasicCli(
    GeneratedParrot.ParrotClient client,
    SlashCommandRegistry commands,
    Interrupts interrupts) : IInterruptListener
{
    private const string Prompt = "> ";

    private readonly Channel<bool> _interrupts =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile bool _busy;
    private volatile bool _interruptRequested;

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

        // Piped stdin is one answer, not a session. Scripts and CI depend on it.
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
            // A selection the registry cannot resolve is a usage error, not a crash.
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

        var cli = new BasicCli(client, commands, interrupts);
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
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);

        // Whether the prompt this call is rendering has started. Before it has,
        // an admission is the prompt the user just typed and is already on
        // screen; after it, an admission is one they typed over the top of a
        // turn, and saying so is the only sign it was taken.
        var started = false;
        var textEndsLine = true;

        while (await MoveNext(stream, cancellationToken).ConfigureAwait(false))
        {
            var published = stream.Current;

            if (!textEndsLine && published.PayloadCase is
                Event.PayloadOneofCase.ToolStarted or
                Event.PayloadOneofCase.ToolFinished or
                Event.PayloadOneofCase.ToolCancelled or
                Event.PayloadOneofCase.ToolError or
                Event.PayloadOneofCase.AgentStarted or
                Event.PayloadOneofCase.AgentFinished or
                Event.PayloadOneofCase.AgentFailed)
            {
                await output.WriteLineAsync().ConfigureAwait(false);
                textEndsLine = true;
            }

            switch (published.PayloadCase)
            {
                case Event.PayloadOneofCase.TurnStarted:
                    started = true;
                    break;

                case Event.PayloadOneofCase.InputAdmitted when started:
                    await output.WriteLineAsync().ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"  queued: {published.InputAdmitted.Content}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TextChunk:
                    await output.WriteAsync(published.TextChunk.Fragment.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    textEndsLine = published.TextChunk.Fragment.EndsWith('\n');
                    break;

                case Event.PayloadOneofCase.ToolStarted:
                    await output.WriteLineAsync(
                        $"  tool started: {published.ToolStarted.ToolName}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolFinished:
                    await output.WriteLineAsync(
                        $"  tool finished: {published.ToolFinished.ToolName}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolCancelled:
                    await output.WriteLineAsync(
                        $"  tool cancelled: {published.ToolCancelled.ToolName}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolError:
                    await output.WriteLineAsync(
                        $"  tool error: {published.ToolError.ToolName}: {published.ToolError.Message}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentStarted:
                    await output.WriteLineAsync(
                        $"  agent started: {published.AgentStarted.Name}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentFinished:
                    await output.WriteLineAsync(
                        $"  agent finished: {published.AgentFinished.Name}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentFailed:
                    await output.WriteLineAsync(
                        $"  agent failed: {published.AgentFailed.Name}: {published.AgentFailed.Message}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.TurnEnded:
                    await output.WriteLineAsync().ConfigureAwait(false);
                    await output.WriteLineAsync(
                        $"  {Summarise(published.TurnEnded)}".AsMemory(), cancellationToken).ConfigureAwait(false);
                    return true;

                case Event.PayloadOneofCase.TurnFailed:
                    await error.WriteLineAsync(
                        $"parrot: {published.TurnFailed.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
                    return false;

                default:
                    break;
            }
        }

        return false;
    }

    // A cancelled stream is an ending, not a failure.
    private static async Task<bool> MoveNext(IAsyncStreamReader<Event> stream, CancellationToken cancellationToken)
    {
        try
        {
            return await stream.MoveNext(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string Summarise(TurnEnded ended) =>
        ended.FinishReason == "length" && ended.OutputTokens > 0
            ? "turn ended: the token budget was spent before any content"
            : $"turn ended ({ended.FinishReason}, {ended.InputTokens} in / {ended.OutputTokens} out)";

    private static SendMessageRequest Message(string userSessionId, string text) =>
        new()
        {
            UserSessionId = userSessionId,
            Text = text,
            Delivery = Delivery.Steer,
        };

    private async Task<int> Once(
        SlashContext context, string prompt, TextWriter output, CancellationToken cancellationToken)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var call = client.Listen(
            new ListenRequest { UserSessionId = context.UserSessionId }, cancellationToken: listening.Token);

        _ = await client.SendMessageAsync(
            Message(context.UserSessionId, prompt), cancellationToken: cancellationToken);

        var completed = await RenderTurn(call.ResponseStream, output, context.Error, listening.Token)
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

    private async Task Render(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        _ = await RenderTurn(stream, output, error, cancellationToken).ConfigureAwait(false);
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
}

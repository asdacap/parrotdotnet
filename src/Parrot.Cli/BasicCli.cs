using System.Threading.Channels;
using Grpc.Core;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class BasicCli(
    GeneratedParrot.ParrotClient client,
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
    TextWriter error) : IInterruptListener
{
    private const string Prompt = "> ";

    private readonly Channel<bool> _interrupts =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Channel<(string UserSessionId, PendingQuestion Pending)> _questions =
        Channel.CreateUnbounded<(string UserSessionId, PendingQuestion Pending)>();

    private readonly PermissionInteractionPresenter _permissions = new(client);

    private volatile bool _busy;
    private volatile bool _interruptRequested;
    private SlashSession? _session;
    private PermissionInteractionPresenter.Session? _permissionSession;

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var text = prompt;

        // Piped stdin is one answer, not a session. Scripts and CI depend on it.
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
                new CreateSessionRequest { Model = model, Mode = mode, InteractivePermissions = text.Length == 0 },
                cancellationToken: cancellationToken);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
        {
            // A selection the registry cannot resolve is a usage error, not a crash.
            await error.WriteLineAsync($"parrot: {failure.Status.Detail}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return CommandDispatcher.ExitFailure;
        }

        foreach (var warning in await ModelAliasWarnings.List(client, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync(warning.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return text.Length > 0
            ? await Once(session.Id, text, output, error, cancellationToken).ConfigureAwait(false)
            : await Loop(session, input, output, error, cancellationToken).ConfigureAwait(false);
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

    internal static Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        RenderTurn(stream, output, error, static (_, _) => Task.CompletedTask, cancellationToken);

    internal static async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        TextWriter output,
        TextWriter error,
        Func<Event, CancellationToken, Task> beforeRender,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(error);
        ArgumentNullException.ThrowIfNull(beforeRender);

        // Whether the prompt this call is rendering has started. Before it has,
        // an admission is the prompt the user just typed and is already on
        // screen; after it, an admission is one they typed over the top of a
        // turn, and saying so is the only sign it was taken.
        var started = false;
        var textEndsLine = true;

        while (await MoveNext(stream, cancellationToken).ConfigureAwait(false))
        {
            var published = stream.Current;
            await beforeRender(published, cancellationToken).ConfigureAwait(false);

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

                case Event.PayloadOneofCase.StatusInjected:
                    await output.WriteLineAsync("↻ Status prompt injected".AsMemory(), cancellationToken)
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
        $"turn ended ({ended.FinishReason}, {ended.InputTokens} total in / {ended.OutputTokens} total out)";

    private static SendMessageRequest Message(string userSessionId, string text) =>
        new()
        {
            UserSessionId = userSessionId,
            Text = text,
            Delivery = Delivery.Steer,
        };

    private async Task<int> Once(
        string userSessionId,
        string prompt,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var call = client.Listen(
            new ListenRequest { UserSessionId = userSessionId }, cancellationToken: listening.Token);

        _ = await client.SendMessageAsync(
            Message(userSessionId, prompt), cancellationToken: cancellationToken);

        var completed = await RenderTurn(call.ResponseStream, output, error, listening.Token).ConfigureAwait(false);

        await listening.CancelAsync().ConfigureAwait(false);
        return completed ? CommandDispatcher.ExitSuccess : CommandDispatcher.ExitFailure;
    }

    private async Task<int> Loop(
        UserSession initialSession,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        if (initialSession.Loaded)
        {
            await output.WriteLineAsync($"Loaded session {initialSession.Id}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
        }

        await output.WriteLineAsync(
            $"parrot {BuildInfo.Version} — /help for commands, /exit to leave".AsMemory(), cancellationToken)
            .ConfigureAwait(false);

        using var application = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var binding = new BasicSlashSessionBinding(
            client, initialSession.Id, this, output, error, application.Token);
        var session = new SlashSession(client, initialSession, configuration, true, binding);
        _session = session;
        _permissionSession = _permissions.Attach(initialSession.Id);
        var reconcilingPermissions = _permissions.Reconcile(application.Token);
        var dialog = new BasicSlashDialog(input, output, error);
        var commands = SlashCommands.Create(
            client,
            dialog,
            session,
            new SlashActivity(() => _busy),
            new ApplicationExit(application),
            credentials,
            oauthClient,
            providerIds);
        var interrupting = Interrupting(session, application.Token);

        interrupts.Install(this);

        try
        {
            await Ready(output, application.Token).ConfigureAwait(false);

            while (!application.IsCancellationRequested)
            {
                if (_permissions.Read() is { } permissionRequest)
                {
                    await _permissions.Present(permissionRequest, dialog, application.Token).ConfigureAwait(false);
                    await Ready(output, application.Token).ConfigureAwait(false);
                    continue;
                }

                if (_questions.Reader.TryRead(out var question))
                {
                    await CompleteQuestion(
                        question.UserSessionId,
                        question.Pending,
                        input,
                        output,
                        error,
                        application.Token).ConfigureAwait(false);
                    await Ready(output, application.Token).ConfigureAwait(false);
                    continue;
                }

                using var reading = CancellationTokenSource.CreateLinkedTokenSource(application.Token);
                var lineTask = input.ReadLineAsync(reading.Token).AsTask();
                var questionTask = _questions.Reader.WaitToReadAsync(application.Token).AsTask();
                var permissionTask = _permissions.WaitToRead(application.Token).AsTask();
                _ = await Task.WhenAny(lineTask, questionTask, permissionTask).ConfigureAwait(false);
                if (!lineTask.IsCompleted)
                {
                    await reading.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        _ = await lineTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    continue;
                }

                var line = await lineTask.ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                var entered = line.Trim();
                if (entered.Length == 0)
                {
                    await Ready(output, application.Token).ConfigureAwait(false);
                    continue;
                }

                if (entered.StartsWith('/'))
                {
                    await commands.Dispatch(entered, application.Token).ConfigureAwait(false);
                    if (application.IsCancellationRequested)
                    {
                        break;
                    }

                    await Ready(output, application.Token).ConfigureAwait(false);
                    continue;
                }

                _busy = true;
                _ = await client.SendMessageAsync(
                    Message(session.Id, entered), cancellationToken: application.Token);
                binding.RenderIfCompleted();
            }
        }
        finally
        {
            interrupts.Remove();
            _ = _interrupts.Writer.TryComplete();
            await application.CancelAsync().ConfigureAwait(false);
            await Task.WhenAll(interrupting, reconcilingPermissions).ConfigureAwait(false);
        }

        return CommandDispatcher.ExitSuccess;
    }

    private async Task Render(
        IAsyncStreamReader<Event> stream, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        PlanCompleted? plan = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var completed = await RenderTurn(
                stream,
                output,
                error,
                (published, _) =>
                {
                    if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted)
                    {
                        _busy = true;
                    }
                    else if (published.PayloadCase == Event.PayloadOneofCase.PlanCompleted)
                    {
                        plan = published.PlanCompleted;
                    }
                    else if (published.PayloadCase == Event.PayloadOneofCase.ToolStarted
                             && string.Equals(published.ToolStarted.ToolName, "question", StringComparison.Ordinal)
                             && _session is not null)
                    {
                        return DiscoverQuestions(_session.Id, _questions.Writer, cancellationToken);
                    }
                    else if (published.PayloadCase == Event.PayloadOneofCase.PermissionPending
                             && _permissionSession is { } permissionSession)
                    {
                        _permissions.Observe(permissionSession, published.PermissionPending);
                    }

                    return Task.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);
            if (!completed)
            {
                _busy = false;
                _interruptRequested = false;
                await Ready(output, cancellationToken).ConfigureAwait(false);
                return;
            }

            _busy = false;
            _interruptRequested = false;

            if (plan is not null)
            {
                await CompletePlan(plan, output, error, cancellationToken).ConfigureAwait(false);
                plan = null;
            }

            await Ready(output, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompletePlan(
        PlanCompleted completed, TextWriter output, TextWriter error, CancellationToken cancellationToken)
    {
        if (completed.Dialog is null || _session is null)
        {
            return;
        }

        await output.WriteLineAsync(completed.Markdown.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteLineAsync(completed.Dialog.Prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.WriteAsync("> ".AsMemory(), cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
        var answer = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        var selected = answer?.Trim() ?? string.Empty;
        var normalized = selected.ToLowerInvariant();
        var choice = completed.Dialog.Choices.FirstOrDefault(item =>
            normalized == item.Value || item.Aliases.Contains(normalized));

        if (choice is not null)
        {
            if (choice.Action?.Mode.Length > 0)
            {
                await _session.SelectMode(choice.Action.Mode, cancellationToken).ConfigureAwait(false);
            }

            if (choice.Action?.Prompt.Length > 0)
            {
                _busy = true;
                _ = await client.SendMessageAsync(Message(_session.Id, choice.Action.Prompt), cancellationToken: cancellationToken);
            }

            return;
        }

        if (selected.Length == 0)
        {
            await error.WriteLineAsync(completed.Dialog.EmptyMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        _busy = true;
        _ = await client.SendMessageAsync(Message(_session.Id, selected), cancellationToken: cancellationToken);
    }

    private async Task DiscoverQuestions(
        string userSessionId,
        ChannelWriter<(string UserSessionId, PendingQuestion Pending)> writer,
        CancellationToken cancellationToken)
    {
        for (var attempts = 0; attempts < 20; attempts++)
        {
            var listed = await client.ListPendingQuestionsAsync(
                new ListPendingQuestionsRequest { UserSessionId = userSessionId },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (listed.Questions.Count > 0)
            {
                foreach (var pending in listed.Questions)
                {
                    await writer.WriteAsync((userSessionId, pending), cancellationToken).ConfigureAwait(false);
                }

                return;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteQuestion(
        string userSessionId,
        PendingQuestion pending,
        TextReader input,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken)
    {
        var reply = new ReplyQuestionRequest
        {
            UserSessionId = userSessionId,
            QuestionRequestId = pending.Id,
        };
        foreach (var question in pending.Questions)
        {
            await output.WriteLineAsync(question.Header.AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(question.Prompt.AsMemory(), cancellationToken).ConfigureAwait(false);
            foreach (var option in question.Options)
            {
                await output.WriteLineAsync($"  {option.Id}: {option.Label}".AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync("answer (or /cancel)".AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("> ".AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var entered = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (entered is null || string.Equals(entered.Trim(), "/cancel", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _ = await client.RejectQuestionAsync(
                        new RejectQuestionRequest
                        {
                            UserSessionId = reply.UserSessionId,
                            QuestionRequestId = pending.Id,
                        },
                        cancellationToken: cancellationToken).ConfigureAwait(false);
                }
                catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
                {
                }

                return;
            }

            var answer = new QuestionAnswer { QuestionId = question.Id };
            if (question.Custom && entered.StartsWith("/custom ", StringComparison.Ordinal))
            {
                answer.Custom = entered[8..].Trim();
            }
            else
            {
                answer.OptionIds.AddRange(entered.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
            }

            reply.Answers.Add(answer);
        }

        try
        {
            _ = await client.ReplyQuestionAsync(reply, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
        {
            await error.WriteLineAsync($"parrot: {failure.Status.Detail}".AsMemory(), cancellationToken).ConfigureAwait(false);
            await _questions.Writer.WriteAsync((userSessionId, pending), cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
        {
        }
    }

    private async Task Interrupting(SlashSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (await _interrupts.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_interrupts.Reader.TryRead(out _))
                {
                    _ = await client.InterruptAsync(
                        new InterruptRequest { UserSessionId = session.Id },
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

    private sealed class BasicSlashSessionBinding(
        GeneratedParrot.ParrotClient client,
        string initialSessionId,
        BasicCli cli,
        TextWriter output,
        TextWriter error,
        CancellationToken lifetimeToken) : ISlashSessionBinding, IAsyncDisposable
    {
        private (CancellationTokenSource Cancellation, AsyncServerStreamingCall<Event> Call) _stream =
            Open(client, initialSessionId, lifetimeToken);

        private Task _rendering = Task.CompletedTask;

        public async Task Replace(UserSession session, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            cancellationToken.ThrowIfCancellationRequested();
            var replacement = Open(client, session.Id, lifetimeToken);
            await CloseStream().ConfigureAwait(false);
            _stream = replacement;
            _rendering = Task.CompletedTask;
            cli._busy = false;
            cli._permissionSession = cli._permissions.Attach(session.Id);
        }

        public void RenderIfCompleted()
        {
            if (_rendering.IsCompleted)
            {
                _rendering = cli.Render(_stream.Call.ResponseStream, output, error, _stream.Cancellation.Token);
            }
        }

        public async ValueTask DisposeAsync() => await CloseStream().ConfigureAwait(false);

        private static (CancellationTokenSource Cancellation, AsyncServerStreamingCall<Event> Call) Open(
            GeneratedParrot.ParrotClient client,
            string userSessionId,
            CancellationToken cancellationToken)
        {
            var streaming = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var call = client.Listen(
                new ListenRequest { UserSessionId = userSessionId },
                cancellationToken: streaming.Token);
            return (streaming, call);
        }

        private async Task CloseStream()
        {
            await _stream.Cancellation.CancelAsync().ConfigureAwait(false);
            await _rendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            _stream.Cancellation.Dispose();
            _stream.Call.Dispose();
        }
    }
}

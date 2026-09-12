using System.Threading.Channels;
using Grpc.Core;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli;

internal sealed class BasicCli(
    GeneratedParrot.ParrotClient client,
    Interrupts interrupts,
    ICredentialStore credentials,
    IOAuthClient oauthClient,
    Configuration configuration,
    IReadOnlyList<string> providerIds,
    string model,
    string mode,
    string prompt,
    bool inputRedirected,
    TextReader input,
    TextWriter output,
    TextWriter error,
    PromptAttachmentUploader attachments,
    IDiagnosticLog diagnostics)
{
    private const string Prompt = "> ";

    private readonly Channel<bool> _interrupts =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private readonly QuestionInteractionPresenter _questions = new(client);

    private readonly PermissionInteractionPresenter _permissions = new(client);
    private QuestionInteractionPresenter.Session? _questionSession;
    private CancellationTokenSource? _questionReconciliationCancellation;
    private Task _reconcilingQuestions = Task.CompletedTask;

    private volatile bool _busy;
    private volatile bool _interruptRequested;
    private ISlashSession? _session;
    private PermissionInteractionPresenter.Session? _permissionSession;

    public UserSession? InitialSession { private get; init; }

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
            session = InitialSession ?? await client.CreateSessionAsync(
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

    internal static async Task WritePlanReport(
        PlanCompleted completed,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(completed);
        ArgumentNullException.ThrowIfNull(output);

        await output.WriteLineAsync(completed.Markdown.AsMemory(), cancellationToken).ConfigureAwait(false);
        var lines = completed.TaskDeclarations.Count > 0
            ? AgentTaskDeclarationFormatter.Format(completed.TaskDeclarations)
            : completed.TaskTree is { RootNodes.Count: > 0 }
                ? AgentTaskProgressFormatter.Format(completed.TaskTree)
                : [];
        foreach (var line in lines)
        {
            await output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        }
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
                Event.PayloadOneofCase.AgentFailed or
                Event.PayloadOneofCase.CompactionStarted or
                Event.PayloadOneofCase.CompactionFinished or
                Event.PayloadOneofCase.CompactionFailed or
                Event.PayloadOneofCase.ActiveWorkReminderInjected or
                Event.PayloadOneofCase.ExitReminderInjected or
                Event.PayloadOneofCase.ContextReminderInjected or
                Event.PayloadOneofCase.FinalProviderRequestPromptInjected or
                Event.PayloadOneofCase.ToolAvailabilityRestoredPromptInjected or
                Event.PayloadOneofCase.SkillLoaded or
                Event.PayloadOneofCase.AgentTaskProgressSnapshot)
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

                case Event.PayloadOneofCase.ActiveWorkReminderInjected:
                    await output.WriteLineAsync("↻ Active work reminder injected".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ExitReminderInjected:
                    await output.WriteLineAsync("↻ Exit reminder injected".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ContextReminderInjected:
                    await output.WriteLineAsync($"↻ Context reminder injected ({published.ContextReminderInjected.UsagePercent}% context used)".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.FinalProviderRequestPromptInjected:
                    await output.WriteLineAsync("↻ Final provider request prompt injected".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ToolAvailabilityRestoredPromptInjected:
                    await output.WriteLineAsync("↻ Tool availability restored prompt injected".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.SkillLoaded:
                    await output.WriteLineAsync(
                        $"↻ Skill loaded: {published.SkillLoaded.Path}".AsMemory(), cancellationToken)
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
                        ($"  agent finished: {published.AgentFinished.Name} " +
                         $"({AgentDurationFormatter.Format(published.AgentFinished.ElapsedMs)})").AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentFailed:
                    await output.WriteLineAsync(
                        $"  agent failed: {published.AgentFailed.Name}: {published.AgentFailed.Message}".AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.CompactionStarted:
                    await output.WriteLineAsync("  compaction started".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.CompactionFinished:
                    await output.WriteLineAsync("  compaction finished".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.CompactionFailed:
                    await output.WriteLineAsync(
                        $"  compaction failed: {published.CompactionFailed.Message}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentTaskProgressSnapshot:
                    foreach (var line in AgentTaskProgressFormatter.Format(published.AgentTaskProgressSnapshot))
                    {
                        await output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
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

    private static async Task<(bool Completed, string? Line)> ReadLine(
        TextReader input,
        CancellationToken cancellationToken,
        params Task[] interruptions)
    {
        using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var line = input.ReadLineAsync(reading.Token).AsTask();
        var completed = await Task.WhenAny([line, .. interruptions]).ConfigureAwait(false);
        if (completed == line)
        {
            return (true, await line.ConfigureAwait(false));
        }

        await reading.CancelAsync().ConfigureAwait(false);
        try
        {
            _ = await line.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return (false, null);
    }

    private static string Summarise(TurnEnded ended) =>
        $"turn ended ({ended.FinishReason}, {ended.InputTokens} total in / {ended.OutputTokens} total out)";

    private bool Interrupt()
    {
        if (!_busy || _interruptRequested)
        {
            return false;
        }

        _interruptRequested = true;
        return _interrupts.Writer.TryWrite(true);
    }

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

        var message = await attachments.Prepare(client, userSessionId, mode, prompt, error, cancellationToken)
            .ConfigureAwait(false);
        if (message is null)
        {
            return CommandDispatcher.ExitFailure;
        }

        _ = await client.SendMessageAsync(message, cancellationToken: cancellationToken);

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
        if (initialSession.Loaded && InitialSession is null)
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
        var session = SlashSession.Create(
            client, initialSession, configuration, true, new CliSlashSessionBinding(binding.Replace));
        _session = session;
        _permissionSession = _permissions.Attach(initialSession.Id);
        _questionSession = _questions.Attach(initialSession.Id);
        _questionReconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(application.Token);
        _reconcilingQuestions = _questions.Reconcile(_questionSession, _questionReconciliationCancellation.Token);
        var reconcilingPermissions = _permissions.Reconcile(application.Token);
        ISlashDialog dialog = new BasicSlashDialog(input, output, error);
        var commands = SlashCommands.Create(
            client,
            dialog,
            session,
            new SlashActivity(() => _busy),
            new ApplicationExit(application),
            credentials,
            oauthClient,
            providerIds,
            static _ => Task.CompletedTask,
            diagnostics);
        var interrupting = Interrupting(session, application.Token);

        var interruptListener = CliInterruptListener.Create(Interrupt);
        interrupts.Install(interruptListener);

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

                if (_questions.Read() is { } question)
                {
                    await CompleteQuestion(
                        question.Session.UserSessionId,
                        question.Pending,
                        input,
                        output,
                        error,
                        application.Token).ConfigureAwait(false);
                    await Ready(output, application.Token).ConfigureAwait(false);
                    continue;
                }

                var questionTask = _questions.WaitToRead(application.Token).AsTask();
                var permissionTask = _permissions.WaitToRead(application.Token).AsTask();
                var (completed, line) = await ReadLine(input, application.Token, questionTask, permissionTask).ConfigureAwait(false);
                if (!completed)
                {
                    continue;
                }

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

                var message = await attachments.Prepare(client, session.Id, session.Mode, entered, error, application.Token)
                    .ConfigureAwait(false);
                if (message is null)
                {
                    await Ready(output, application.Token).ConfigureAwait(false);
                    continue;
                }

                _busy = true;
                _ = await client.SendMessageAsync(message, cancellationToken: application.Token);
                binding.RenderIfCompleted();
            }
        }
        finally
        {
            interrupts.Remove();
            _ = _interrupts.Writer.TryComplete();
            await application.CancelAsync().ConfigureAwait(false);
            if (_questionReconciliationCancellation is { } questionCancellation)
            {
                await questionCancellation.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(interrupting, reconcilingPermissions, _reconcilingQuestions).ConfigureAwait(false);
                questionCancellation.Dispose();
            }
            else
            {
                await Task.WhenAll(interrupting, reconcilingPermissions).ConfigureAwait(false);
            }
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

        await WritePlanReport(completed, output, cancellationToken).ConfigureAwait(false);
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
                _ = await client.SendMessageAsync(new SendMessageRequest { UserSessionId = _session.Id, Text = choice.Action.Prompt, Delivery = Delivery.Steer }, cancellationToken: cancellationToken);
            }

            return;
        }

        if (selected.Length == 0)
        {
            await error.WriteLineAsync(completed.Dialog.EmptyMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        _busy = true;
        _ = await client.SendMessageAsync(new SendMessageRequest { UserSessionId = _session.Id, Text = selected, Delivery = Delivery.Steer }, cancellationToken: cancellationToken);
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
                await output.WriteLineAsync($"  {option}".AsMemory(), cancellationToken).ConfigureAwait(false);
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

            reply.Answers.Add(new QuestionAnswer { Text = entered });
        }

        try
        {
            _ = await client.ReplyQuestionAsync(reply, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
        {
            await error.WriteLineAsync($"parrot: {failure.Status.Detail}".AsMemory(), cancellationToken).ConfigureAwait(false);
            if (_questionSession is { } questionSession)
            {
                _questions.Retry(questionSession, pending);
            }
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
        {
        }
    }

    private async Task ReplaceQuestionSession(string userSessionId, CancellationToken cancellationToken)
    {
        if (_questionReconciliationCancellation is { } previousCancellation)
        {
            await previousCancellation.CancelAsync().ConfigureAwait(false);
            await _reconcilingQuestions.ConfigureAwait(false);
            previousCancellation.Dispose();
        }

        _questionSession = _questions.Attach(userSessionId);
        _questionReconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _reconcilingQuestions = _questions.Reconcile(_questionSession, _questionReconciliationCancellation.Token);
    }

    private async Task Interrupting(ISlashSession session, CancellationToken cancellationToken)
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
        CancellationToken lifetimeToken) : IAsyncDisposable
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
            await cli.ReplaceQuestionSession(session.Id, cancellationToken).ConfigureAwait(false);
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
            try
            {
                await _rendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stream.Cancellation.IsCancellationRequested)
            {
            }
            finally
            {
                _stream.Cancellation.Dispose();
                _stream.Call.Dispose();
            }
        }
    }
}

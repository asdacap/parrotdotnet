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
    CallInvoker initialInvoker,
    Interrupts interrupts,
    ICredentialStore credentials,
    CredentialPresets credentialPresets,
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

    private readonly TerminalSessionCallInvoker _routing = new(initialInvoker);

    private QuestionInteractionPresenter.Session? _questionSession;
    private CancellationTokenSource? _questionReconciliationCancellation;
    private Task _reconcilingQuestions = Task.CompletedTask;
    private CancellationTokenSource? _permissionReconciliationCancellation;
    private Task _reconcilingPermissions = Task.CompletedTask;

    private volatile bool _busy;
    private volatile bool _interruptRequested;
    private ISlashSession? _session;
    private PermissionInteractionPresenter.Session? _permissionSession;

    public UserSession? InitialSession { private get; init; }

    public ITerminalSessionNavigation? Navigation { private get; init; }

    private GeneratedParrot.ParrotClient Client => _routing.Client;

    private QuestionInteractionPresenter? QuestionPresenter { get; set; }

    private PermissionInteractionPresenter? PermissionPresenter { get; set; }

    private QuestionInteractionPresenter Questions => QuestionPresenter ??= new(Client);

    private PermissionInteractionPresenter Permissions => PermissionPresenter ??= new(Client);

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
            session = InitialSession ?? await Client.CreateSessionAsync(
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

        foreach (var warning in await ModelAliasWarnings.List(Client, cancellationToken).ConfigureAwait(false))
        {
            await output.WriteLineAsync(warning.AsMemory(), cancellationToken).ConfigureAwait(false);
        }

        return text.Length > 0
            ? await Once(session.Id, text, cancellationToken).ConfigureAwait(false)
            : await Loop(session, cancellationToken).ConfigureAwait(false);
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

    internal static Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        TextWriter output,
        TextWriter error,
        Func<Event, CancellationToken, Task> beforeRender,
        CancellationToken cancellationToken)
        => RenderTurnCore(stream, output, error, beforeRender, static () => false, cancellationToken);

    internal static Task<bool> RenderReplayTurn(
        IAsyncStreamReader<Event> stream,
        TextWriter output,
        TextWriter error,
        Func<Event, CancellationToken, Task> beforeRender,
        Func<bool> isReplaying,
        CancellationToken cancellationToken) =>
        RenderTurnCore(stream, output, error, beforeRender, isReplaying, cancellationToken);

    private static async Task<bool> RenderTurnCore(
        IAsyncStreamReader<Event> stream,
        TextWriter output,
        TextWriter error,
        Func<Event, CancellationToken, Task> beforeRender,
        Func<bool> isReplaying,
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
                Event.PayloadOneofCase.ExitReminderChanged or
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

                case Event.PayloadOneofCase.ExitReminderChanged:
                    await output.WriteLineAsync(
                        $"↻ {ExitReminderNotice(published.ExitReminderChanged)}".AsMemory(), cancellationToken)
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

                case Event.PayloadOneofCase.AgentStarted when published.AgentStarted.ParentAgentSessionId.Length > 0:
                    await output.WriteLineAsync(
                        $"  agent started: {published.AgentStarted.Name}".AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentFinished when published.AgentFinished.ParentAgentSessionId.Length > 0:
                    await output.WriteLineAsync(
                        ($"  agent finished: {published.AgentFinished.Name} " +
                         $"({AgentDurationFormatter.Format(published.AgentFinished.ElapsedMs)})").AsMemory(),
                        cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.AgentFailed when published.AgentFailed.ParentAgentSessionId.Length > 0:
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
                    break;

                case Event.PayloadOneofCase.PlanCompleted when isReplaying():
                    await WritePlanReport(published.PlanCompleted, output, cancellationToken).ConfigureAwait(false);
                    break;

                case Event.PayloadOneofCase.ModeTurnCompleted:
                    if (!isReplaying())
                    {
                        return true;
                    }

                    break;

                case Event.PayloadOneofCase.TurnFailed:
                    await error.WriteLineAsync(
                        $"parrot: {published.TurnFailed.Message}".AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (!isReplaying())
                    {
                        return false;
                    }

                    break;

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

    private static string ExitReminderNotice(ExitReminderChanged changed) =>
        changed.StateCase == ExitReminderChanged.StateOneofCase.Description
            ? $"Exit reminder set: {changed.Title}: {changed.Description}"
            : $"Exit reminder cleared: {changed.Title}";

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
        CancellationToken cancellationToken)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var call = Client.Listen(
            new ListenRequest { UserSessionId = userSessionId }, cancellationToken: listening.Token);

        var message = await attachments.Prepare(Client, userSessionId, mode, prompt, Delivery.Queue, error, cancellationToken)
            .ConfigureAwait(false);
        if (message is null)
        {
            return CommandDispatcher.ExitFailure;
        }

        _ = await Client.SendMessageAsync(message, cancellationToken: cancellationToken);

        var completed = await RenderTurn(call.ResponseStream, output, error, listening.Token).ConfigureAwait(false);

        await listening.CancelAsync().ConfigureAwait(false);
        return completed ? CommandDispatcher.ExitSuccess : CommandDispatcher.ExitFailure;
    }

    private async Task<int> Loop(UserSession initialSession, CancellationToken cancellationToken)
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
        await using var binding = new BasicSlashSessionBinding(Client, initialSession.Id, this, application.Token);
        var session = SlashSession.Create(
            Client, initialSession, configuration, true, new CliSlashSessionBinding(binding.Replace));
        _session = session;
        AttachInteractions(initialSession.Id, application.Token);
        using var navigation = Navigation ?? new ExplicitRemoteTerminalSessionNavigation(initialInvoker);
        using var sessionController = new TerminalSessionController(
            navigation, _routing, session, initialSession, binding.ReplaceExisting);
        ISlashDialog dialog = new BasicSlashDialog(input, output, error);
        var commands = SlashCommands.CreateTerminal(
            Client,
            dialog,
            session,
            new SlashActivity(() => _busy),
            new ApplicationExit(application),
            credentials,
            credentialPresets,
            oauthClient,
            providerIds,
            static _ => Task.CompletedTask,
            diagnostics,
            sessionController);
        var interrupting = Interrupting(session, application.Token);

        var interruptListener = CliInterruptListener.Create(Interrupt);
        interrupts.Install(interruptListener);

        try
        {
            await Ready(application.Token).ConfigureAwait(false);

            while (!application.IsCancellationRequested)
            {
                if (Permissions.Read() is { } permissionRequest)
                {
                    await Permissions.Present(permissionRequest, dialog, application.Token).ConfigureAwait(false);
                    await Ready(application.Token).ConfigureAwait(false);
                    continue;
                }

                if (Questions.Read() is { } question)
                {
                    await CompleteQuestion(question.Session.UserSessionId, question.Pending, application.Token)
                        .ConfigureAwait(false);
                    await Ready(application.Token).ConfigureAwait(false);
                    continue;
                }

                var questionTask = Questions.WaitToRead(application.Token).AsTask();
                var permissionTask = Permissions.WaitToRead(application.Token).AsTask();
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
                    await Ready(application.Token).ConfigureAwait(false);
                    continue;
                }

                if (entered.StartsWith('/'))
                {
                    await commands.Dispatch(entered, application.Token).ConfigureAwait(false);
                    if (application.IsCancellationRequested)
                    {
                        break;
                    }

                    await Ready(application.Token).ConfigureAwait(false);
                    continue;
                }

                var message = await attachments.Prepare(Client, session.Id, session.Mode, entered, _busy ? Delivery.Steer : Delivery.Queue, error, application.Token)
                    .ConfigureAwait(false);
                if (message is null)
                {
                    await Ready(application.Token).ConfigureAwait(false);
                    continue;
                }

                _busy = true;
                _ = await Client.SendMessageAsync(message, cancellationToken: application.Token);
                binding.RenderIfCompleted();
            }
        }
        finally
        {
            interrupts.Remove();
            _ = _interrupts.Writer.TryComplete();
            await application.CancelAsync().ConfigureAwait(false);
            await StopInteractions().ConfigureAwait(false);
            await interrupting.ConfigureAwait(false);
            await binding.DisposeAsync().ConfigureAwait(false);
        }

        return CommandDispatcher.ExitSuccess;
    }

    private async Task Render(IAsyncStreamReader<Event> stream, Func<bool> isReplaying, CancellationToken cancellationToken)
    {
        PlanCompleted? plan = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            var completed = await RenderReplayTurn(
                stream,
                output,
                error,
                (published, _) =>
                {
                    if (isReplaying())
                    {
                        return Task.CompletedTask;
                    }

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
                        Permissions.Observe(permissionSession, published.PermissionPending);
                    }

                    return Task.CompletedTask;
                },
                isReplaying,
                cancellationToken).ConfigureAwait(false);
            _busy = false;
            _interruptRequested = false;
            if (!completed)
            {
                await Ready(cancellationToken).ConfigureAwait(false);
                return;
            }

            if (plan is not null)
            {
                await CompletePlan(plan, cancellationToken).ConfigureAwait(false);
                plan = null;
            }

            await Ready(cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompletePlan(PlanCompleted completed, CancellationToken cancellationToken)
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
                _ = await Client.SendMessageAsync(new SendMessageRequest { UserSessionId = _session.Id, Text = choice.Action.Prompt, Delivery = Delivery.Queue }, cancellationToken: cancellationToken);
            }

            return;
        }

        if (selected.Length == 0)
        {
            await error.WriteLineAsync(completed.Dialog.EmptyMessage.AsMemory(), cancellationToken).ConfigureAwait(false);
            return;
        }

        _busy = true;
        _ = await Client.SendMessageAsync(new SendMessageRequest { UserSessionId = _session.Id, Text = selected, Delivery = Delivery.Queue }, cancellationToken: cancellationToken);
    }

    private async Task CompleteQuestion(string userSessionId, PendingQuestion pending, CancellationToken cancellationToken)
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
                var line = option.Description.Length == 0
                    ? $"  {option.Label}"
                    : $"  {option.Label} — {option.Description}";
                await output.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }

            await output.WriteLineAsync("answer (or /cancel)".AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.WriteAsync("> ".AsMemory(), cancellationToken).ConfigureAwait(false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(false);
            var entered = await input.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (entered is null || string.Equals(entered.Trim(), "/cancel", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _ = await Client.RejectQuestionAsync(
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
            _ = await Client.ReplyQuestionAsync(reply, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
        {
            await error.WriteLineAsync($"parrot: {failure.Status.Detail}".AsMemory(), cancellationToken).ConfigureAwait(false);
            if (_questionSession is { } questionSession)
            {
                Questions.Retry(questionSession, pending);
            }
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
        {
        }
    }

    private void AttachInteractions(string userSessionId, CancellationToken lifetimeToken)
    {
        _permissionSession = Permissions.Attach(userSessionId);
        _questionSession = Questions.Attach(userSessionId);
        _questionReconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _permissionReconciliationCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken);
        _reconcilingQuestions = Questions.Reconcile(_questionSession, _questionReconciliationCancellation.Token);
        _reconcilingPermissions = Permissions.Reconcile(_permissionReconciliationCancellation.Token);
    }

    private async Task StopInteractions()
    {
        if (_questionReconciliationCancellation is { } questions)
        {
            await questions.CancelAsync().ConfigureAwait(false);
        }

        if (_permissionReconciliationCancellation is { } permissions)
        {
            await permissions.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(_reconcilingQuestions.WaitAsync(CancellationToken.None), _reconcilingPermissions.WaitAsync(CancellationToken.None)).ConfigureAwait(false);
        }
        finally
        {
            _questionReconciliationCancellation?.Dispose();
            _permissionReconciliationCancellation?.Dispose();
            _questionReconciliationCancellation = null;
            _permissionReconciliationCancellation = null;
            _reconcilingQuestions = Task.CompletedTask;
            _reconcilingPermissions = Task.CompletedTask;
        }
    }

    private async Task Interrupting(ISlashSession session, CancellationToken cancellationToken)
    {
        try
        {
            while (await _interrupts.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_interrupts.Reader.TryRead(out _))
                {
                    _ = await Client.InterruptAsync(
                        new InterruptRequest { UserSessionId = session.Id },
                        cancellationToken: cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task Ready(CancellationToken cancellationToken)
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
        CancellationToken lifetimeToken) : IAsyncDisposable
    {
        private PreparedSessionStream _stream = PreparedSessionStream.OpenLive(client, initialSessionId, lifetimeToken);
        private string _sessionId = initialSessionId;
        private Task _rendering = Task.CompletedTask;
        private bool _disposed;

        public async Task Replace(UserSession session, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(session);
            cancellationToken.ThrowIfCancellationRequested();
            var replacement = PreparedSessionStream.OpenLive(client, session.Id, lifetimeToken);
            try
            {
                await cli.StopInteractions().ConfigureAwait(false);
                await CloseStream().ConfigureAwait(false);
                _stream = replacement;
                _sessionId = session.Id;
                _rendering = Task.CompletedTask;
                cli._busy = false;
                cli.AttachInteractions(session.Id, lifetimeToken);
            }
            catch
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }

        public async Task ReplaceExisting(
            UserSession session,
            GeneratedParrot.ParrotClient targetClient,
            Action commit,
            CancellationToken cancellationToken)
        {
            var replacement = await PreparedSessionStream.Open(targetClient, session.Id, lifetimeToken, cancellationToken)
                .ConfigureAwait(false);
            var previousInvoker = cli._routing.Target;
            var previousClient = new GeneratedParrot.ParrotClient(previousInvoker);
            var previousId = _sessionId;
            var replacing = false;
            var previousStreamStopped = false;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                replacing = true;
                await cli.StopInteractions().ConfigureAwait(false);
                previousStreamStopped = true;
                await CloseStream().ConfigureAwait(false);
                commit();
                _stream = replacement;
                _sessionId = session.Id;
                _rendering = Task.CompletedTask;
                cli._busy = false;
                cli._interruptRequested = false;
                cli.AttachInteractions(session.Id, lifetimeToken);
                replacement.SetReplayCompletion(busy => cli._busy = busy);
                RenderIfCompleted();
            }
            catch
            {
                await replacement.DisposeAsync().ConfigureAwait(false);
                if (replacing)
                {
                    try
                    {
                        await cli.StopInteractions().ConfigureAwait(false);
                        if (!previousStreamStopped)
                        {
                            await CloseStream().ConfigureAwait(false);
                        }
                    }
                    finally
                    {
                        cli._routing.Target = previousInvoker;
                        _stream = PreparedSessionStream.OpenLive(previousClient, previousId, lifetimeToken);
                        _sessionId = previousId;
                        _rendering = Task.CompletedTask;
                        cli.AttachInteractions(previousId, lifetimeToken);
                        RenderIfCompleted();
                    }
                }

                throw;
            }
        }

        public void RenderIfCompleted()
        {
            if (_rendering.IsCompleted)
            {
                _rendering = cli.Render(_stream.Call.ResponseStream, () => _stream.IsReplaying, _stream.Token);
            }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_disposed)
            {
                _disposed = true;
                await CloseStream().ConfigureAwait(false);
            }
        }

        private async Task CloseStream()
        {
            await _stream.Cancel().ConfigureAwait(false);
            try
            {
                await _rendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stream.Token.IsCancellationRequested)
            {
            }
            catch (RpcException failure) when (_stream.Token.IsCancellationRequested && failure.StatusCode == StatusCode.Cancelled)
            {
            }
            finally
            {
                await _stream.DisposeAsync().ConfigureAwait(false);
            }
        }
    }
}

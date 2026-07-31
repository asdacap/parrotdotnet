using System.Threading.Channels;
using Grpc.Core;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Protocol;
using GeneratedParrot = Parrot.Protocol.Parrot;

namespace Parrot.Cli.Enhanced;

internal sealed class EnhancedCli(
    GeneratedParrot.ParrotClient client,
    Interrupts interrupts,
    EnhancedChatRequest request,
    ICredentialStore credentials,
    OpenAiOAuthClient oauthClient,
    Configuration configuration,
    IReadOnlyList<string> providerIds,
    ITerminal terminal,
    ToolPresenterRegistry toolPresenters,
    EnhancedTurnRenderer turnRenderer,
    Func<TimeSpan, CancellationToken, Task> delaySubmit) : IInterruptListener, ISlashSessionBinding
{
    private const string DisableBracketedPaste = "\u001b[?2004l";
    private const string DisableKeyboardEnhancement = "\u001b[<u";
    private const string EnableBracketedPaste = "\u001b[?2004h";
    private const string EnableKeyboardEnhancement = "\u001b[>1u";
    private const int MaximumVisibleCompletions = 8;
    private const string Prompt = TerminalIcons.UserPrompt + " ";
    private static readonly TimeSpan QuestionReconciliationInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SubmitDelay = TimeSpan.FromMilliseconds(100);

    private readonly Channel<bool> _interrupts =
        Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    private volatile bool _busy;
    private volatile bool _interruptRequested;
    private Func<UserSession, CancellationToken, Task>? _replaceSession;

    public async Task<int> Run(CancellationToken cancellationToken)
    {
        var text = request.Prompt;

        UserSession session;

        try
        {
            request.Session.InteractivePermissions = request.Prompt.Length == 0;
            session = await client.CreateSessionAsync(request.Session, cancellationToken: cancellationToken);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
        {
            await terminal.Error.WriteLineAsync($"parrot: {failure.Status.Detail}".AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            return CommandDispatcher.ExitFailure;
        }

        using var applicationExit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        return await Loop(
            session,
            text,
            terminal.Output,
            text.Length > 0,
            applicationExit,
            applicationExit.Token).ConfigureAwait(false);
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

    Task ISlashSessionBinding.Replace(UserSession session, CancellationToken cancellationToken) =>
        _replaceSession is { } replace
            ? replace(session, cancellationToken)
            : throw new InvalidOperationException("the enhanced session is not running");

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

    private static SendMessageRequest Message(string userSessionId, string text) =>
        new()
        {
            UserSessionId = userSessionId,
            Text = text,
            Delivery = Delivery.Steer,
        };

    private static async Task<string?> NextMode(
        GeneratedParrot.ParrotClient client,
        string current,
        EnhancedSlashDialog dialog,
        CancellationToken cancellationToken)
    {
        var listed = await client
            .ListModesAsync(new ListModesRequest(), cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (listed.Modes.Count == 0)
        {
            await dialog.ShowError("no modes are available", cancellationToken).ConfigureAwait(false);
            return null;
        }

        var next = 0;

        for (var index = 0; index < listed.Modes.Count; index++)
        {
            if (!string.Equals(listed.Modes[index].Id, current, StringComparison.Ordinal))
            {
                continue;
            }

            next = (index + 1) % listed.Modes.Count;
            break;
        }

        return listed.Modes[next].Id;
    }

    private async Task<int> Loop(
        UserSession initialSession,
        string initialPrompt,
        TextWriter output,
        bool exitOnFirstCompletion,
        CancellationTokenSource applicationExit,
        CancellationToken cancellationToken)
    {
        using var listening = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var session = new SlashSession(client, initialSession, configuration, !exitOnFirstCompletion, this);
        var activeSession = initialSession;
        var rendering = Task.CompletedTask;
        var interrupting = Interrupting(session, listening.Token);
        var editor = new IncrementalEditor(Prompt, 64 * 1024);
        var exiting = false;
        var firstTurnCompleted = !exitOnFirstCompletion;
        var pendingSubmit = (PendingSubmit?)null;
        var binding = (EnhancedListenBinding?)null;
        var planRequests = Channel.CreateUnbounded<PlanCompletionRequest>();
        var questionRequests = Channel.CreateUnbounded<(string UserSessionId, PendingQuestion Pending)>();
        var discoveredQuestionRequests = new HashSet<string>(StringComparer.Ordinal);
        var permissions = new PermissionInteractionPresenter(client);
        PermissionInteractionPresenter.Session? permissionSession = null;
        var reconcilingPermissions = Task.CompletedTask;

        async Task ObserveRenderingEvent(Event published, CancellationToken eventToken)
        {
            var discoverQuestions = published.PayloadCase == Event.PayloadOneofCase.ToolStarted
                && string.Equals(published.ToolStarted.ToolName, "question", StringComparison.Ordinal);
            if (discoverQuestions)
            {
                await DiscoverQuestions(
                    activeSession.Id,
                    questionRequests.Writer,
                    discoveredQuestionRequests,
                    eventToken).ConfigureAwait(false);
            }

            if (published.PayloadCase == Event.PayloadOneofCase.PermissionPending
                && permissionSession is { } attached)
            {
                permissions.Observe(attached, published.PermissionPending);
            }
        }

        Task StartRenderingTurn(CancellationToken eventToken)
        {
            eventToken.ThrowIfCancellationRequested();
            _busy = true;
            return Task.CompletedTask;
        }

        Task FinishRenderingTurn(CancellationToken eventToken)
        {
            eventToken.ThrowIfCancellationRequested();
            _busy = false;
            _interruptRequested = false;
            return Task.CompletedTask;
        }

        async Task CompleteRenderingPlan(PlanCompleted completed, CancellationToken eventToken)
        {
            var pending = new PlanCompletionRequest(completed);
            await planRequests.Writer.WriteAsync(pending, eventToken).ConfigureAwait(false);
            await pending.Answered.Task.WaitAsync(eventToken).ConfigureAwait(false);
        }

        using var renderingSession = new EnhancedRenderingSession(
            turnRenderer,
            toolPresenters,
            new TerminalFrameRenderer(
                output,
                terminal.GetColumns,
                new TerminalPalette(terminal.Color),
                TerminalFrameRenderer.DefaultLiveRows,
                TerminalFrameRenderer.DefaultInputRows,
                configuration.InlineDiff),
            session,
            [editor.Prompt],
            ObserveRenderingEvent,
            StartRenderingTurn,
            FinishRenderingTurn,
            () => _busy,
            CompleteRenderingPlan,
            exitOnFirstCompletion);
        var updating = renderingSession.RunUpdates(listening.Token);

        async Task StartTurn(string entered)
        {
            _busy = true;
            await renderingSession.BeginTurn(
                ImmediateScrollbackValue.User(entered),
                [editor.Prompt],
                binding?.Token ?? cancellationToken,
                cancellationToken).ConfigureAwait(false);
            _ = await client.SendMessageAsync(
                Message(session.Id, entered), cancellationToken: cancellationToken);
        }

        async Task StartRendering(AsyncServerStreamingCall<Event> activeCall, CancellationToken streamToken)
        {
            firstTurnCompleted = await renderingSession.Run(
                activeCall.ResponseStream,
                streamToken).ConfigureAwait(false);
        }

        async Task ReplaceSession(UserSession replacement, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (binding is null)
            {
                throw new InvalidOperationException("the enhanced session stream is not running");
            }

            await renderingSession.StopSpinner().ConfigureAwait(false);
            await binding.DisposeAsync().ConfigureAwait(false);
            await renderingSession.ResetForSession(CancellationToken.None).ConfigureAwait(false);
            discoveredQuestionRequests.Clear();
            while (questionRequests.Reader.TryRead(out _))
            {
            }

            activeSession = replacement;
            permissionSession = permissions.Attach(replacement.Id);
            binding = EnhancedListenBinding.Open(client, replacement.Id, StartRendering, listening.Token);
            rendering = binding.Rendering;
            _busy = false;
            _interruptRequested = false;
        }

        _replaceSession = ReplaceSession;
        var liveInput = new EnhancedLiveInputHost(terminal, renderingSession.ReplaceInput);
        var dialog = new EnhancedSlashDialog(liveInput);
        var commands = SlashCommands.Create(
            client,
            dialog,
            session,
            new SlashActivity(() => _busy),
            new ApplicationExit(applicationExit),
            credentials,
            oauthClient,
            providerIds);
        var completion = new SlashCommandCompletion(commands);

        Task DrawPrompt(CancellationToken token)
        {
            completion.Refresh(editor.Prompt.Text);
            var start = Math.Clamp(
                completion.Selected - MaximumVisibleCompletions + 1,
                0,
                Math.Max(0, completion.Commands.Count - MaximumVisibleCompletions));
            var items = completion.Commands
                .Skip(start)
                .Take(MaximumVisibleCompletions)
                .Select((command, index) => (ILiveBufferItem)new PickerOptionValue(
                    command.Name,
                    command.Summary,
                    start + index == completion.Selected))
                .Append(editor.Prompt)
                .ToList();
            return renderingSession.ReplaceInput(items, token);
        }

        void DeferSubmit() => pendingSubmit = new PendingSubmit(delaySubmit, SubmitDelay, cancellationToken);

        async Task ClearDeferredSubmit(bool cancel)
        {
            var pending = pendingSubmit ?? throw new InvalidOperationException("there is no deferred submit");
            pendingSubmit = null;
            if (cancel)
            {
                await pending.Cancel().ConfigureAwait(false);
            }
            else
            {
                await pending.Complete().ConfigureAwait(false);
            }
        }

        async Task ApplyPromptKey(TerminalKey key, bool defer)
        {
            if (defer && key.Kind == TerminalKeyKind.Submit)
            {
                DeferSubmit();
                await DrawPrompt(cancellationToken).ConfigureAwait(false);
                return;
            }

            string? entered;
            if (key.Kind == TerminalKeyKind.Up && completion.Commands.Count > 0)
            {
                completion.SelectPrevious();
                entered = null;
            }
            else if (key.Kind == TerminalKeyKind.Down && completion.Commands.Count > 0)
            {
                completion.SelectNext();
                entered = null;
            }
            else if (key.Kind == TerminalKeyKind.Complete)
            {
                var accepted = completion.Accept(editor.Prompt.Text);
                if (accepted is not null)
                {
                    editor.Replace(accepted);
                }

                entered = null;
            }
            else
            {
                entered = editor.Apply(key);
            }

            await DrawPrompt(cancellationToken).ConfigureAwait(false);
            if (entered is null || entered.Length == 0)
            {
                return;
            }

            if (entered.StartsWith('/'))
            {
                try
                {
                    await commands.Dispatch(entered, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    await DrawPrompt(CancellationToken.None).ConfigureAwait(false);
                }

                if (applicationExit.IsCancellationRequested)
                {
                    exiting = true;
                }
            }
            else
            {
                await StartTurn(entered).ConfigureAwait(false);
            }
        }

        interrupts.Install(this);

        try
        {
            await SetBracketedPaste(output, true, cancellationToken).ConfigureAwait(false);
            await SetKeyboardEnhancement(output, true, cancellationToken).ConfigureAwait(false);
            permissionSession = permissions.Attach(activeSession.Id);
            reconcilingPermissions = permissions.Reconcile(listening.Token);
            binding = EnhancedListenBinding.Open(client, activeSession.Id, StartRendering, listening.Token);
            rendering = binding.Rendering;
            if (initialSession.Loaded && !exitOnFirstCompletion)
            {
                await renderingSession.Commit(
                    ImmediateScrollbackValue.Muted([$"Loaded session {session.Id}"]),
                    cancellationToken).ConfigureAwait(false);
            }

            var aliasWarnings = await ModelAliasWarnings.List(client, cancellationToken).ConfigureAwait(false);
            if (aliasWarnings.Count > 0)
            {
                await renderingSession.Commit(
                    ImmediateScrollbackValue.Muted(aliasWarnings), cancellationToken).ConfigureAwait(false);
            }

            await renderingSession.RefreshReady(cancellationToken).ConfigureAwait(false);
            await DrawPrompt(cancellationToken).ConfigureAwait(false);
            if (initialPrompt.Length > 0)
            {
                await StartTurn(initialPrompt).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested && !exiting)
            {
                if (updating.IsCompleted)
                {
                    await updating.ConfigureAwait(false);
                }

                if (exitOnFirstCompletion && rendering.IsCompleted)
                {
                    await rendering.ConfigureAwait(false);
                    exiting = true;
                    continue;
                }

                if (permissions.Read() is { } permissionRequest)
                {
                    await permissions.Present(permissionRequest, dialog, cancellationToken).ConfigureAwait(false);
                    await DrawPrompt(CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                if (planRequests.Reader.TryRead(out var planRequest))
                {
                    await CompletePlan(planRequest, dialog, session, cancellationToken).ConfigureAwait(false);
                    await DrawPrompt(CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                if (questionRequests.Reader.TryRead(out var questionRequest))
                {
                    await CompleteQuestion(
                        questionRequest.UserSessionId,
                        questionRequest.Pending,
                        dialog,
                        cancellationToken).ConfigureAwait(false);
                    await DrawPrompt(CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var keyTask = liveInput.ReadKey(reading.Token).AsTask();
                var planTask = planRequests.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var questionTask = questionRequests.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var permissionTask = permissions.WaitToRead(cancellationToken).AsTask();
                var completionTask = exitOnFirstCompletion ? rendering : Task.Delay(Timeout.Infinite, cancellationToken);
                var submitTask = pendingSubmit?.Task ?? Task.Delay(Timeout.Infinite, cancellationToken);
                _ = await Task.WhenAny(
                    keyTask,
                    planTask,
                    questionTask,
                    permissionTask,
                    completionTask,
                    updating,
                    submitTask).ConfigureAwait(false);
                var key = (TerminalKey?)null;
                if (keyTask.IsCompletedSuccessfully)
                {
                    key = await keyTask.ConfigureAwait(false);
                }
                else
                {
                    await reading.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        key = await keyTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }
                }

                if (key is null)
                {
                    if (pendingSubmit?.Task.IsCompleted == true)
                    {
                        await ClearDeferredSubmit(false).ConfigureAwait(false);
                        await ApplyPromptKey(new TerminalKey(TerminalKeyKind.Submit), false).ConfigureAwait(false);
                    }

                    continue;
                }

                var received = key.Value;
                if (received.Kind is TerminalKeyKind.Escape or TerminalKeyKind.Interrupt)
                {
                    _ = Interrupted();
                }
                else if (received.Kind == TerminalKeyKind.EndOfFile && editor.IsEmpty)
                {
                    exiting = true;
                    break;
                }
                else if (received.Kind == TerminalKeyKind.Mode)
                {
                    var mode = await NextMode(client, session.Mode, dialog, cancellationToken)
                        .ConfigureAwait(false);
                    if (mode is not null)
                    {
                        await session.SelectMode(mode, cancellationToken).ConfigureAwait(false);
                    }
                }
                else if (received.Kind == TerminalKeyKind.EndOfFile)
                {
                    await ApplyPromptKey(received, false).ConfigureAwait(false);
                }
                else
                {
                    if (pendingSubmit is not null)
                    {
                        await ClearDeferredSubmit(true).ConfigureAwait(false);
                        _ = editor.Apply(new TerminalKey(TerminalKeyKind.Newline));
                        completion.Refresh(editor.Prompt.Text);
                    }

                    await ApplyPromptKey(received, true).ConfigureAwait(false);
                }

                if (!_busy && !exiting)
                {
                    await renderingSession.RefreshReady(cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                if (pendingSubmit is not null)
                {
                    await ClearDeferredSubmit(true).ConfigureAwait(false);
                }

                _replaceSession = null;
                interrupts.Remove();
                _ = _interrupts.Writer.TryComplete();
                await renderingSession.StopSpinner().ConfigureAwait(false);
                await listening.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(updating, rendering).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await renderingSession.Clear(CancellationToken.None).ConfigureAwait(false);
                    }
                    finally
                    {
                        await Task.WhenAll(interrupting, reconcilingPermissions).ConfigureAwait(false);
                    }
                }
            }
            finally
            {
                if (binding is not null)
                {
                    await binding.DisposeAsync().ConfigureAwait(false);
                }

                await SetKeyboardEnhancement(output, false, CancellationToken.None).ConfigureAwait(false);
                await SetBracketedPaste(output, false, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return exitOnFirstCompletion && !firstTurnCompleted
            ? CommandDispatcher.ExitFailure
            : CommandDispatcher.ExitSuccess;
    }

    private async Task DiscoverQuestions(
        string userSessionId,
        ChannelWriter<(string UserSessionId, PendingQuestion Pending)> writer,
        HashSet<string> discovered,
        CancellationToken cancellationToken)
    {
        for (var attempts = 0; attempts < 20; attempts++)
        {
            var listed = await client.ListPendingQuestionsAsync(
                new ListPendingQuestionsRequest { UserSessionId = userSessionId },
                cancellationToken: cancellationToken).ConfigureAwait(false);
            var added = false;
            foreach (var pending in listed.Questions)
            {
                if (discovered.Add(pending.Id))
                {
                    added = true;
                    await writer.WriteAsync((userSessionId, pending), cancellationToken).ConfigureAwait(false);
                }
            }

            if (added)
            {
                return;
            }

            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task CompleteQuestion(
        string userSessionId,
        PendingQuestion pending,
        EnhancedSlashDialog dialog,
        CancellationToken cancellationToken)
    {
        using var lifetime = new PendingQuestionLifetime(
            client,
            userSessionId,
            pending.Id,
            QuestionReconciliationInterval);
        using var monitoring = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var interaction = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            lifetime.ClosedToken);
        var reconciling = lifetime.Run(monitoring.Token);
        var reply = new ReplyQuestionRequest
        {
            UserSessionId = userSessionId,
            QuestionRequestId = pending.Id,
        };

        try
        {
            foreach (var question in pending.Questions)
            {
                var choices = question.Options
                    .Select(option => new SlashDialogOption(option.Id, option.Label, option.Id))
                    .ToList();
                const string customId = "__custom__";
                if (question.Custom)
                {
                    choices.Add(new SlashDialogOption(customId, "Custom answer", "Write an answer"));
                }

                var selected = await dialog.Select(
                    $"{question.Header} {question.Prompt}".Trim(),
                    choices,
                    interaction.Token).ConfigureAwait(false);
                if (lifetime.IsClosed)
                {
                    return;
                }

                if (selected is null)
                {
                    await StopReconciling().ConfigureAwait(false);
                    if (!lifetime.IsClosed)
                    {
                        await RejectQuestion(pending.Id, userSessionId, cancellationToken).ConfigureAwait(false);
                    }

                    return;
                }

                var answer = new QuestionAnswer { QuestionId = question.Id };
                if (selected.Id == customId)
                {
                    var custom = await dialog.ReadText(question.Prompt, interaction.Token).ConfigureAwait(false);
                    if (lifetime.IsClosed)
                    {
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(custom))
                    {
                        await StopReconciling().ConfigureAwait(false);
                        if (!lifetime.IsClosed)
                        {
                            await RejectQuestion(pending.Id, userSessionId, cancellationToken).ConfigureAwait(false);
                        }

                        return;
                    }

                    answer.Custom = custom.Trim();
                }
                else
                {
                    answer.OptionIds.Add(selected.Id);
                }

                if (lifetime.IsClosed)
                {
                    return;
                }

                reply.Answers.Add(answer);
            }

            await StopReconciling().ConfigureAwait(false);
            if (lifetime.IsClosed)
            {
                return;
            }

            try
            {
                _ = await client.ReplyQuestionAsync(reply, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException failure) when (failure.StatusCode == StatusCode.InvalidArgument)
            {
                await dialog.ShowError(failure.Status.Detail, cancellationToken).ConfigureAwait(false);
            }
            catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
            {
            }
        }
        catch (OperationCanceledException) when (lifetime.IsClosed && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            await StopReconciling().ConfigureAwait(false);
        }

        async Task StopReconciling()
        {
            await monitoring.CancelAsync().ConfigureAwait(false);
            await reconciling.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task RejectQuestion(
        string questionRequestId,
        string userSessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await client.RejectQuestionAsync(
                new RejectQuestionRequest
                {
                    UserSessionId = userSessionId,
                    QuestionRequestId = questionRequestId,
                },
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (RpcException failure) when (failure.StatusCode == StatusCode.NotFound)
        {
        }
    }

    private async Task CompletePlan(
        PlanCompletionRequest request,
        EnhancedSlashDialog dialog,
        SlashSession session,
        CancellationToken cancellationToken)
    {
        try
        {
            var completed = request.Completed;
            if (completed.Dialog is null)
            {
                return;
            }

            var definition = completed.Dialog;
            var choices = definition.Choices
                .Select(choice => new SlashDialogOption(choice.Value, choice.Value, choice.Description))
                .ToList();
            if (definition.CustomChoice.Length > 0)
            {
                var description = definition.CustomDescription.Length > 0
                    ? definition.CustomDescription
                    : "Provide feedback and revise";
                choices.Add(new SlashDialogOption(
                    definition.CustomChoice,
                    definition.CustomChoice,
                    description));
            }

            if (choices.Count == 0)
            {
                return;
            }

            var selected = await dialog.Select(definition.Prompt, choices, cancellationToken).ConfigureAwait(false);
            if (selected is null)
            {
                return;
            }

            if (selected.Id == definition.CustomChoice)
            {
                var feedback = await dialog.ReadText(definition.CustomPrompt, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(feedback))
                {
                    return;
                }

                _busy = true;
                _ = await client.SendMessageAsync(
                    Message(session.Id, feedback.Trim()), cancellationToken: cancellationToken).ConfigureAwait(false);
                return;
            }

            var choice = definition.Choices.FirstOrDefault(item => item.Value == selected.Id);
            if (choice is null)
            {
                return;
            }

            if (choice.Action?.Mode.Length > 0)
            {
                await session.SelectMode(choice.Action.Mode, cancellationToken).ConfigureAwait(false);
            }

            if (choice.Action?.Prompt.Length > 0)
            {
                _busy = true;
                _ = await client.SendMessageAsync(
                    Message(session.Id, choice.Action.Prompt), cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _ = request.Answered.TrySetResult();
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
}

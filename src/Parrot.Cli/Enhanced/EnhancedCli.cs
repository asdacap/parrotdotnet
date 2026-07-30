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
    Func<TimeSpan, CancellationToken, Task> delaySubmit) : IInterruptListener, ISlashSessionBinding
{
    private const string DisableBracketedPaste = "\u001b[?2004l";
    private const string DisableKeyboardEnhancement = "\u001b[<u";
    private const string EnableBracketedPaste = "\u001b[?2004h";
    private const string EnableKeyboardEnhancement = "\u001b[>1u";
    private const int MaximumVisibleCompletions = 8;
    private const string Prompt = TerminalIcons.UserPrompt + " ";
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

    internal Task<bool> RenderTurn(IAsyncStreamReader<Event> stream, CancellationToken cancellationToken) =>
        RenderTurn(stream, null, true, null, null, null, new ForegroundTurn(), cancellationToken);

    internal Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        Func<Event, CancellationToken, Task> beforeRender,
        CancellationToken cancellationToken) =>
        RenderTurn(stream, beforeRender, true, null, null, null, new ForegroundTurn(), cancellationToken);

    internal async Task<bool> RenderTurn(
        IAsyncStreamReader<Event> stream,
        Func<Event, CancellationToken, Task>? beforeRender,
        bool renderActivityEvents,
        Func<Event, CancellationToken, Task>? afterRender,
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task>? draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task>? commit,
        ForegroundTurn foreground,
        CancellationToken cancellationToken)
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
                new TerminalPalette(terminal.Color),
                configuration.InlineDiff);
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
            terminal.Color,
            foreground);

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
        var currentInput = (IReadOnlyList<ILiveBufferItem>)[editor.Prompt];
        var currentBody = (IReadOnlyList<ILiveBufferItem>)[];
        var currentQueueRows = (IReadOnlyList<ILiveBufferItem>)[];
        var usage = new RuntimeUsageTracker();
        var foregroundForModeline = new ForegroundTurn();
        var mainAgentActivity = string.Empty;
        var modelineActivity = string.Empty;
        var modelineFrame = 0;
        var modelineTools = new HashSet<string>(StringComparer.Ordinal);
        var currentModeline = CreateModeline();
        using var composing = new SemaphoreSlim(1, 1);
        var palette = new TerminalPalette(terminal.Color);
        var renderer = new TerminalFrameRenderer(
            output,
            terminal.GetColumns,
            palette,
            TerminalFrameRenderer.DefaultLiveRows,
            TerminalFrameRenderer.DefaultInputRows,
            configuration.InlineDiff);
        var updates = new LiveUpdateScheduler(DrawScheduled);
        var updating = updates.Run(listening.Token);
        var spinner = new TerminalSpinner(DrawInitialBody);
        var exiting = false;
        var firstTurnCompleted = !exitOnFirstCompletion;
        var pendingSubmit = (PendingSubmit?)null;
        var binding = (EnhancedListenBinding?)null;
        using var spinnerLifecycle = new SemaphoreSlim(1, 1);
        var spinnerRendering = Task.CompletedTask;
        var spinnerStop = (TaskCompletionSource?)null;
        var planRequests = Channel.CreateUnbounded<PlanCompletionRequest>();
        var questionRequests = Channel.CreateUnbounded<(string UserSessionId, PendingQuestion Pending)>();
        var discoveredQuestionRequests = new HashSet<string>(StringComparer.Ordinal);
        var permissions = new PermissionInteractionPresenter(client);
        PermissionInteractionPresenter.Session? permissionSession = null;
        var reconcilingPermissions = Task.CompletedTask;

        IReadOnlyList<ILiveBufferItem> Snapshot() =>
            [.. currentBody, .. currentQueueRows, currentModeline, .. currentInput];

        ModelineValue CreateModeline()
        {
            var runtime = usage.Current;
            var right = new[]
            {
                runtime.FormatContext() is { Length: > 0 } context
                    ? $"{session.Model} ({context})"
                    : session.Model,
                runtime.FormatTokens(),
                runtime.FormatCost(),
            };
            var activityLabel = modelineTools.Count > 0
                ? modelineActivity
                : mainAgentActivity.Length > 0
                    ? mainAgentActivity
                    : modelineActivity;
            var activity = activityLabel.Length == 0
                ? string.Empty
                : $"{TerminalIcons.SpinnerFrames[modelineFrame % TerminalIcons.SpinnerFrames.Length]} {activityLabel}";
            return new ModelineValue(
                session.Mode,
                activity,
                string.Join(" · ", right.Where(value => value.Length > 0)));
        }

        void ObserveModelineActivity(Event published)
        {
            if (foregroundForModeline.IsTerminal(published)
                || (published.PayloadCase == Event.PayloadOneofCase.TurnStarted
                    && foregroundForModeline.IsMain(published.AgentSessionId)))
            {
                modelineTools.Clear();
                modelineActivity = string.Empty;
            }
            else if (published.PayloadCase == Event.PayloadOneofCase.ToolStarted
                && foregroundForModeline.IsMain(published.AgentSessionId)
                && toolPresenters.Describe(published.ToolStarted.ToolName).Modeline)
            {
                _ = modelineTools.Add(published.ToolStarted.ToolCallId);
                modelineActivity = $"Working: {published.ToolStarted.ToolName}";
            }
            else if (published.PayloadCase is Event.PayloadOneofCase.ToolFinished
                     or Event.PayloadOneofCase.ToolCancelled
                     or Event.PayloadOneofCase.ToolError)
            {
                var toolCallId = published.PayloadCase switch
                {
                    Event.PayloadOneofCase.ToolFinished => published.ToolFinished.ToolCallId,
                    Event.PayloadOneofCase.ToolCancelled => published.ToolCancelled.ToolCallId,
                    _ => published.ToolError.ToolCallId,
                };
                if (modelineTools.Remove(toolCallId) && modelineTools.Count == 0)
                {
                    modelineActivity = string.Empty;
                }
            }
        }

        async Task ReplaceQueueRows(IReadOnlyList<QueueState> queues, CancellationToken token)
        {
            var rows = queues
                .Where(queue => queue.Name.Length > 0 && queue.ItemCount > 0)
                .GroupBy(queue => queue.Name, StringComparer.Ordinal)
                .Select(group => group.Last())
                .OrderBy(queue => queue.Name, StringComparer.Ordinal)
                .Select(queue => (ILiveBufferItem)new QueueLiveBufferItem(
                    queue.Name,
                    queue.Description,
                    queue.ItemCount))
                .ToArray();
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                currentQueueRows = rows;
            }
            finally
            {
                _ = composing.Release();
            }

            _ = updates.Invalidate();
        }

        async Task UpdateMainAgentActivity(string activity, CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                mainAgentActivity = activity;
                currentModeline = CreateModeline();
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task ReplaceBody(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                currentBody = [.. items];
                modelineFrame++;
                currentModeline = CreateModeline();
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task DrawInitialBody(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                currentBody = [.. items];
                modelineFrame++;
                currentModeline = CreateModeline();
                await renderer.Draw(Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task DrawScheduled(CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
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
                currentModeline = CreateModeline();
                await renderer.Commit(scrollback, Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }
        }

        async Task ReplaceInput(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
        {
            await composing.WaitAsync(token).ConfigureAwait(false);
            try
            {
                currentInput = [.. items];
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

        async Task StopSpinner()
        {
            await spinnerLifecycle.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                _ = spinnerStop?.TrySetResult();
                await spinnerRendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                spinnerStop = null;
                spinnerRendering = Task.CompletedTask;
            }
            finally
            {
                _ = spinnerLifecycle.Release();
            }
        }

        async Task StartSpinner(CancellationToken token)
        {
            await spinnerLifecycle.WaitAsync(token).ConfigureAwait(false);
            try
            {
                _ = spinnerStop?.TrySetResult();
                await spinnerRendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
                spinnerStop = new(TaskCreationOptions.RunContinuationsAsynchronously);
                var stop = spinnerStop;
                spinnerRendering = spinner.Run(
                    index => new SpinnerValue("thinking", index),
                    async (preserve, lifetimeToken) =>
                    {
                        await stop.Task.WaitAsync(lifetimeToken).ConfigureAwait(false);
                        await preserve().ConfigureAwait(false);
                    },
                    token);
            }
            finally
            {
                _ = spinnerLifecycle.Release();
            }
        }

        async Task StartTurn(string entered)
        {
            _busy = true;
            var committed = ImmediateScrollbackValue.User(entered);
            await composing.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                modelineActivity = "Thinking…";
                currentModeline = CreateModeline();
                currentInput = [editor.Prompt];
                await renderer.Commit(committed, Snapshot(), CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _ = composing.Release();
            }

            await StartSpinner(binding?.Token ?? cancellationToken).ConfigureAwait(false);
            _ = await client.SendMessageAsync(
                Message(session.Id, entered), cancellationToken: cancellationToken);
        }

        async Task StartRendering(AsyncServerStreamingCall<Event> activeCall, CancellationToken streamToken)
        {
            firstTurnCompleted = await RenderRaw(
                new QueueSnapshotStreamReader(activeCall.ResponseStream, ReplaceQueueRows),
                ReplaceBody,
                CommitBody,
                UpdateMainAgentActivity,
                async (published, eventToken) =>
            {
                var discoverQuestions = published.PayloadCase == Event.PayloadOneofCase.ToolStarted
                    && string.Equals(published.ToolStarted.ToolName, "question", StringComparison.Ordinal);
                await composing.WaitAsync(eventToken).ConfigureAwait(false);
                try
                {
                    foregroundForModeline.Observe(published);
                    usage.Observe(published);
                    ObserveModelineActivity(published);
                    currentModeline = CreateModeline();
                }
                finally
                {
                    _ = composing.Release();
                }

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
            },
                async readyToken =>
            {
                await composing.WaitAsync(readyToken).ConfigureAwait(false);
                try
                {
                    mainAgentActivity = string.Empty;
                    modelineActivity = string.Empty;
                    currentModeline = CreateModeline();
                }
                finally
                {
                    _ = composing.Release();
                }

                _ = updates.Invalidate();
            },
                StopSpinner,
                updates.Invalidate,
                async (completed, eventToken) =>
            {
                var pending = new PlanCompletionRequest(completed);
                await planRequests.Writer.WriteAsync(pending, eventToken).ConfigureAwait(false);
                await pending.Answered.Task.WaitAsync(eventToken).ConfigureAwait(false);
            },
                exitOnFirstCompletion,
                streamToken).ConfigureAwait(false);
        }

        async Task ReplaceSession(UserSession replacement, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (binding is null)
            {
                throw new InvalidOperationException("the enhanced session stream is not running");
            }

            await StopSpinner().ConfigureAwait(false);
            await binding.DisposeAsync().ConfigureAwait(false);
            await ReplaceQueueRows([], CancellationToken.None).ConfigureAwait(false);
            discoveredQuestionRequests.Clear();
            while (questionRequests.Reader.TryRead(out _))
            {
            }

            activeSession = replacement;
            permissionSession = permissions.Attach(replacement.Id);
            binding = EnhancedListenBinding.Open(client, replacement.Id, StartRendering, listening.Token);
            rendering = binding.Rendering;
            usage.Reset();
            foregroundForModeline.Reset();
            modelineTools.Clear();
            mainAgentActivity = string.Empty;
            modelineActivity = string.Empty;
            currentModeline = CreateModeline();
            _busy = false;
            _interruptRequested = false;
        }

        _replaceSession = ReplaceSession;
        var liveInput = new EnhancedLiveInputHost(terminal, ReplaceInput);
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
            return ReplaceInput(items, token);
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
                await renderer.Commit(
                    ImmediateScrollbackValue.Muted([$"Loaded session {session.Id}"]),
                    Snapshot(),
                    cancellationToken).ConfigureAwait(false);
            }

            var aliasWarnings = await ModelAliasWarnings.List(client, cancellationToken).ConfigureAwait(false);
            if (aliasWarnings.Count > 0)
            {
                await renderer.Commit(
                    ImmediateScrollbackValue.Muted(aliasWarnings), Snapshot(), cancellationToken).ConfigureAwait(false);
            }

            await DrawState(CreateModeline(), cancellationToken).ConfigureAwait(false);
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
                    modelineActivity = string.Empty;
                    await DrawState(CreateModeline(), cancellationToken).ConfigureAwait(false);
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
                await StopSpinner().ConfigureAwait(false);
                await listening.CancelAsync().ConfigureAwait(false);
                try
                {
                    await Task.WhenAll(updating, rendering).ConfigureAwait(false);
                }
                finally
                {
                    try
                    {
                        await Clear(CancellationToken.None).ConfigureAwait(false);
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
        var reply = new ReplyQuestionRequest
        {
            UserSessionId = userSessionId,
            QuestionRequestId = pending.Id,
        };

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
                cancellationToken).ConfigureAwait(false);
            if (selected is null)
            {
                await RejectQuestion(pending.Id, userSessionId, cancellationToken).ConfigureAwait(false);
                return;
            }

            var answer = new QuestionAnswer { QuestionId = question.Id };
            if (selected.Id == customId)
            {
                var custom = await dialog.ReadText(question.Prompt, cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(custom))
                {
                    await RejectQuestion(pending.Id, userSessionId, cancellationToken).ConfigureAwait(false);
                    return;
                }

                answer.Custom = custom.Trim();
            }
            else
            {
                answer.OptionIds.Add(selected.Id);
            }

            reply.Answers.Add(answer);
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

    private async Task<bool> RenderRaw(
        IAsyncStreamReader<Event> stream,
        Func<IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> draw,
        Func<IScrollbackItem, IReadOnlyList<ILiveBufferItem>, CancellationToken, Task> commit,
        Func<string, CancellationToken, Task> updateMainAgentActivity,
        Func<Event, CancellationToken, Task> observe,
        Func<CancellationToken, Task> ready,
        Func<Task> stopSpinner,
        Func<bool> invalidate,
        Func<PlanCompleted, CancellationToken, Task> completePlan,
        bool exitOnFirstCompletion,
        CancellationToken cancellationToken)
    {
        var foreground = new ForegroundTurn();
        using var activity = new RawActivityView(
            draw,
            commit,
            toolPresenters,
            updateMainAgentActivity,
            invalidate);
        using var animating = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var animation = activity.Run(animating.Token);

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var failed = false;
                PlanCompleted? plan = null;

                async Task BeforeRender(Event published, CancellationToken token)
                {
                    foreground.Observe(published);
                    if (published.PayloadCase == Event.PayloadOneofCase.TurnStarted
                        && foreground.IsMain(published.AgentSessionId))
                    {
                        _busy = true;
                    }
                    else if (published.PayloadCase == Event.PayloadOneofCase.TurnFailed
                             && foreground.IsTerminal(published))
                    {
                        failed = true;
                    }
                    else if (published.PayloadCase == Event.PayloadOneofCase.PlanCompleted
                             && foreground.IsMain(published.AgentSessionId))
                    {
                        plan = published.PlanCompleted;
                    }

                    await observe(published, token).ConfigureAwait(false);
                    await stopSpinner().ConfigureAwait(false);
                    await activity.Prepare(published, token).ConfigureAwait(false);
                }

                var completed = await RenderTurn(
                    stream,
                    BeforeRender,
                    false,
                    async (published, eventToken) =>
                    {
                        await activity.Render(published, eventToken).ConfigureAwait(false);

                        // Events update cached state or commit scrollback. This delayed invalidation
                        // coalesces event bursts; user interaction can still redraw immediately.
                        _ = invalidate();
                    },
                    activity.ReplaceContent,
                    activity.CommitContent,
                    foreground,
                    cancellationToken).ConfigureAwait(false);

                if (!completed && !failed)
                {
                    return false;
                }

                _busy = false;
                _interruptRequested = false;
                if (plan is not null)
                {
                    if (plan.Markdown.Length > 0)
                    {
                        await activity.CommitContent(
                            new MarkdownScrollbackValue(plan.Markdown),
                            [],
                            cancellationToken).ConfigureAwait(false);
                    }

                    await completePlan(plan, cancellationToken).ConfigureAwait(false);
                }

                if (exitOnFirstCompletion)
                {
                    return completed;
                }

                if (!cancellationToken.IsCancellationRequested && !_busy)
                {
                    await ready(cancellationToken).ConfigureAwait(false);
                }
            }

            return false;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            await animating.CancelAsync().ConfigureAwait(false);
            await animation.ConfigureAwait(false);
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

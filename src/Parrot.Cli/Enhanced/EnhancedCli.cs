using System.Threading.Channels;
using Grpc.Core;
using Parrot.Auth;
using Parrot.Cli.Commands;
using Parrot.Cli.Enhanced.Tools;
using Parrot.Config;
using Parrot.Protocol;
using Parrot.State;
using Parrot.Store;
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
    ToolPresenterRegistry toolPresenters) : IInterruptListener, ISlashSessionBinding
{
    private const string DisableBracketedPaste = "\u001b[?2004l";
    private const string DisableKeyboardEnhancement = "\u001b[<u";
    private const string EnableBracketedPaste = "\u001b[?2004h";
    private const string EnableKeyboardEnhancement = "\u001b[>1u";
    private const string Prompt = "$ ";

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

        await using var view = new EnhancedTurnView(
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
        var session = new SlashSession(client, initialSession, configuration, this);
        var rendering = Task.CompletedTask;
        var interrupting = Interrupting(session, listening.Token);
        var editor = new IncrementalEditor(Prompt, 64 * 1024);
        var currentInput = (IReadOnlyList<ILiveBufferItem>)[editor.Prompt];
        var currentBody = (IReadOnlyList<ILiveBufferItem>)[];
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
        var spinner = new TerminalSpinner(DrawBody);
        var exiting = false;
        var firstTurnCompleted = false;
        var streaming = (CancellationTokenSource?)null;
        var call = (AsyncServerStreamingCall<Event>?)null;
        var planRequests = Channel.CreateUnbounded<PlanCompletionRequest>();

        IReadOnlyList<ILiveBufferItem> Snapshot() => [.. currentBody, currentModeline, .. currentInput];

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

        async Task DrawBody(IReadOnlyList<ILiveBufferItem> items, CancellationToken token)
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

        Task DrawPrompt(CancellationToken token) => ReplaceInput([editor.Prompt], token);

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

        async Task StartTurn(string entered, AsyncServerStreamingCall<Event> activeCall)
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

            _ = await client.SendMessageAsync(
                Message(session.Id, entered), cancellationToken: cancellationToken);
            if (rendering.IsCompleted)
            {
                rendering = spinner.Run(
                    index => new SpinnerValue("thinking", index),
                    async (stopSpinner, token) => firstTurnCompleted = await RenderRaw(
                        activeCall.ResponseStream,
                        DrawBody,
                        CommitBody,
                        UpdateMainAgentActivity,
                        async (published, eventToken) =>
                        {
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

                            await DrawState(currentModeline, readyToken).ConfigureAwait(false);
                        },
                        stopSpinner,
                        async (completed, eventToken) =>
                        {
                            var pending = new PlanCompletionRequest(completed);
                            await planRequests.Writer.WriteAsync(pending, eventToken).ConfigureAwait(false);
                            await pending.Answered.Task.WaitAsync(eventToken).ConfigureAwait(false);
                        },
                        exitOnFirstCompletion,
                        token).ConfigureAwait(false),
                    streaming?.Token ?? cancellationToken);
            }
        }

        async Task ReplaceSession(UserSession replacement, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (streaming is null || call is null)
            {
                throw new InvalidOperationException("the enhanced session stream is not running");
            }

            var replacementStreaming = CancellationTokenSource.CreateLinkedTokenSource(listening.Token);
            var replacementCall = client.Listen(
                new ListenRequest { UserSessionId = replacement.Id },
                cancellationToken: replacementStreaming.Token);
            await streaming.CancelAsync().ConfigureAwait(false);
            await rendering.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            streaming.Dispose();
            call.Dispose();
            streaming = replacementStreaming;
            call = replacementCall;
            rendering = Task.CompletedTask;
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
            providerIds,
            new SessionIndex(StatePaths.ResolveFromEnvironment().State));

        interrupts.Install(this);

        try
        {
            await SetBracketedPaste(output, true, cancellationToken).ConfigureAwait(false);
            await SetKeyboardEnhancement(output, true, cancellationToken).ConfigureAwait(false);
            streaming = CancellationTokenSource.CreateLinkedTokenSource(listening.Token);
            call = client.Listen(
                new ListenRequest { UserSessionId = session.Id }, cancellationToken: streaming.Token);
            var aliasWarnings = await ModelAliasWarnings.List(client, cancellationToken).ConfigureAwait(false);
            if (aliasWarnings.Count > 0)
            {
                await renderer.Commit(
                    ImmediateScrollbackValue.Muted(aliasWarnings), Snapshot(), cancellationToken).ConfigureAwait(false);
            }

            await DrawState(CreateModeline(), cancellationToken).ConfigureAwait(false);
            if (initialPrompt.Length > 0)
            {
                await StartTurn(initialPrompt, call).ConfigureAwait(false);
            }

            while (!cancellationToken.IsCancellationRequested && !exiting)
            {
                if (exitOnFirstCompletion && rendering.IsCompleted)
                {
                    await rendering.ConfigureAwait(false);
                    exiting = true;
                    continue;
                }

                if (planRequests.Reader.TryRead(out var planRequest))
                {
                    await CompletePlan(planRequest, dialog, session, cancellationToken).ConfigureAwait(false);
                    await DrawPrompt(CancellationToken.None).ConfigureAwait(false);
                    continue;
                }

                using var reading = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var keyTask = liveInput.ReadKey(reading.Token).AsTask();
                var planTask = planRequests.Reader.WaitToReadAsync(cancellationToken).AsTask();
                var completionTask = exitOnFirstCompletion ? rendering : Task.Delay(Timeout.Infinite, cancellationToken);
                _ = await Task.WhenAny(keyTask, planTask, completionTask).ConfigureAwait(false);
                if (!keyTask.IsCompleted)
                {
                    await reading.CancelAsync().ConfigureAwait(false);
                    try
                    {
                        _ = await keyTask.ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                    }

                    continue;
                }

                var key = await keyTask.ConfigureAwait(false);
                if (key.Kind is TerminalKeyKind.Escape or TerminalKeyKind.Interrupt)
                {
                    _ = Interrupted();
                }
                else if (key.Kind == TerminalKeyKind.EndOfFile && editor.IsEmpty)
                {
                    exiting = true;
                    break;
                }
                else if (key.Kind == TerminalKeyKind.Mode)
                {
                    var mode = await NextMode(client, session.Mode, dialog, cancellationToken)
                        .ConfigureAwait(false);
                    if (mode is not null)
                    {
                        await session.SelectMode(mode, cancellationToken).ConfigureAwait(false);
                    }
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

                        if (entered.StartsWith('/'))
                        {
                            try
                            {
                                await commands.Dispatch(entered, cancellationToken).ConfigureAwait(false);
                            }
                            finally
                            {
                                await ReplaceInput([editor.Prompt], CancellationToken.None).ConfigureAwait(false);
                            }

                            if (applicationExit.IsCancellationRequested)
                            {
                                exiting = true;
                                break;
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
                    modelineActivity = string.Empty;
                    await DrawState(CreateModeline(), cancellationToken).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            try
            {
                _replaceSession = null;
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
        Func<PlanCompleted, CancellationToken, Task> completePlan,
        bool exitOnFirstCompletion,
        CancellationToken cancellationToken)
    {
        var spinning = true;
        var foreground = new ForegroundTurn();
        using var activity = new RawActivityView(draw, commit, toolPresenters, updateMainAgentActivity);
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
                    if (spinning)
                    {
                        spinning = false;
                        await stopSpinner().ConfigureAwait(false);
                    }

                    await activity.Prepare(published, token).ConfigureAwait(false);
                }

                var completed = await RenderTurn(
                    stream,
                    BeforeRender,
                    false,
                    activity.Render,
                    activity.DrawContent,
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
                    await activity.Redraw(cancellationToken).ConfigureAwait(false);
                }
            }

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

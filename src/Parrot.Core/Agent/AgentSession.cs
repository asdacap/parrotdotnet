using System.Diagnostics;
using System.Text;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Statuses;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

// Owns the drain and the turn loop. A turn is a loop iteration here, not a
// nested Run: it calls the provider, executes any tool calls the model asks
// for, feeds the results back, and repeats until the model stops asking.
//
// At most one drain owns the session (principle 2). A prompt arriving while one
// runs is admitted and promoted by that drain, never answered by a second one:
// two drains would be two writers on one history and two provider calls billed
// for the same conversation.
//
// The drain state below -- the task, its cancellation, the wake flag and the
// state itself -- is shared with whatever thread admits or interrupts, and
// _drainGate is the whole of its synchronisation.
internal sealed class AgentSession(
    AgentIdentity identity,
    AgentSessionParentScope parentScope,
    ModelSelector model,
    ModelRouter router,
    EventBroker eventBroker,
    EventRepository eventRepository,
    [InjectionTag("toolFactories")] IReadOnlyList<IToolFactory> toolFactories,
    ToolDefinitionCatalog toolDefinitions,
    ISystemPrompt systemPrompt,
    ToolOutputBlobStore toolOutputBlobs,
    Compactor compactor,
    PromptTemplateCatalog promptTemplates,
    ChildQuestionCoordinator childQuestions,
    ExitReminder exitReminder,
    IMode mode,
    [InjectionTag("turnCompletionCallbacks")] IReadOnlyList<IAgentTurnCompletionCallback> turnCompletionCallbacks,
    AgentSessionSecurity security,
    RuntimeStatus status,
    ChildRegistry childRegistry,
    AgentQueues queues,
    AgentSessionActivity activity,
    CancellationToken lifetime)
{
    private const string InterruptedFinish = "interrupted";
    private const int MaxAgentMessageBytes = 1024 * 1024;
    private const int MaxAgentResultBytes = 1024 * 1024;

    private readonly string _runawayMessage = promptTemplates.Render("agent-session.runaway", []);
    private readonly string _finalProviderRequestPrompt = promptTemplates.Render("agent-session.final-request", []);
    private readonly string _toolAvailabilityRestoredPrompt = promptTemplates.Render("agent-session.tools-restored", []);
    private readonly string _interruptedResult = promptTemplates.Render("agent-session.interrupted-result", []);
    private readonly string _interruptedNote = promptTemplates.Render("agent-session.interrupted-note", []);

    // What the conversation records where the answer would have been. Without
    // it the history ends on the prompt that was stopped, and the next drain
    // reads that as a question still owed an answer -- so interrupting a turn
    // would start it again.

    // The conversation, carried across turns so the agent remembers. The system
    // context is sampled once per epoch and prefixed at each turn.
    private readonly List<LLMMessage> _history = RestoreHistory(eventRepository, identity.SessionId);
    private readonly Queue<ForcedCompactionRequest> _forcedCompactions = [];
    private readonly IReadOnlyList<IAgentTurnCompletionCallback> _turnCompletionCallbacks =
        turnCompletionCallbacks ?? throw new ArgumentNullException(nameof(turnCompletionCallbacks));

    private readonly Lock _executionGate = new();
    private readonly Lock _drainGate = new();
    private readonly Lock _selectionGate = new();
    private readonly ISystemPrompt _systemPrompt = systemPrompt
        ?? throw new ArgumentNullException(nameof(systemPrompt));

    private AgentStatistics _statistics = eventRepository.LatestStatistics(identity.SessionId)
        ?? new AgentStatistics(0, 0, 0, 0, 0, 0, 0);

    private DrainState _state;

    private AgentSelection _selection = new(model, mode, security.Policy());
    private ToolSnapshot? _tools;

    private bool _epochInitialized;
    private bool _initialStatusPending = identity.Depth > 0;
    private Task<AgentExecution> _drain = Task.FromResult(AgentExecution.Succeeded(string.Empty));
    private CancellationTokenSource? _drainCancellation;
    private bool _wake;
    private bool _aborted;
    private bool _started;
    private Task<AgentExecution> _execution = Task.FromResult(AgentExecution.Succeeded(string.Empty));
    private TaskCompletionSource<IncomingActivity>? _incomingInputWait;

    // Set while an interrupt is unwinding a drain. It says who disposes the
    // drain's cancellation: normally the drain does when it settles, but an
    // interrupter still holding it to cancel would then be cancelling a
    // disposed source, so it hands that duty over for the one case where the
    // two overlap.
    private bool _stopping;

    internal string SessionId => identity.SessionId;

    internal string Name => identity.Name;

    internal string ParentSessionId => identity.ParentSessionId;

    internal string ParentSessionName => identity.ParentSessionName;

    // How deep this session sits below the root. The registry refuses a child
    // beyond its recursion limit.
    internal int Depth => identity.Depth;

    internal AgentIdentity Identity => identity;

    internal ChildRegistry ChildRegistry { get; } = childRegistry;

    internal AgentSessionActivity Activity { get; } = activity
        ?? throw new ArgumentNullException(nameof(activity));

    internal AgentSelection Selection()
    {
        lock (_selectionGate)
        {
            return _selection;
        }
    }

    internal void UpdateSelection(ModelSelector selectedModel, IMode mode)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(mode);

        lock (_selectionGate)
        {
            _selection = new AgentSelection(
                selectedModel,
                mode,
                mode.SecurityProfile);
        }
    }

    // Accepts a prompt. It does not run it: the prompt becomes durable here
    // (principle 1) and joins the conversation when the drain reaches the
    // boundary its delivery asks for. Waking is not waiting -- the caller is
    // told the prompt was taken, not what the model said about it.
    internal void Recover() => _ = Wake(null);

    // Stops the turn in flight and returns once the drain has unwound, so a
    // caller that sends again cannot race the turn it just stopped.
    //
    // Input admitted and not yet promoted outlives the interrupt: the drain
    // resumes for it rather than making the user ask a second time.
    internal async Task Interrupt(CancellationToken cancellationToken)
    {
        Task draining;
        CancellationTokenSource? stopping = null;

        lock (_drainGate)
        {
            if (_drainCancellation is null)
            {
                return;
            }

            draining = _drain;

            // Somebody is already stopping this drain. Wait for the same
            // unwinding rather than cancelling and disposing it twice.
            if (!_stopping)
            {
                _state = DrainState.Interrupting;
                Activity.ChangeState(DrainState.Interrupting);

                // Cleared behind the same gate as the capture: a wake that
                // survived it would restart the drain this is stopping.
                _wake = false;
                _stopping = true;
                stopping = _drainCancellation;
            }
        }

        // Outside the gate: cancelling runs the token's callbacks on this
        // thread, and the drain needs the gate to settle.
        if (stopping is not null)
        {
            await stopping.CancelAsync().ConfigureAwait(false);
        }

        await draining.WaitAsync(cancellationToken).ConfigureAwait(false);

        if (stopping is not null)
        {
            lock (_drainGate)
            {
                _stopping = false;
            }

            // The drain left it alone because _stopping was set, and it has
            // finished, so nothing else can be holding it.
            stopping.Dispose();
        }

        if (!_aborted && eventRepository.HasPendingInputs(SessionId))
        {
            _ = Wake(new IncomingActivity(IncomingActivityKind.Input, string.Empty));
        }
    }

    // Waits for the drain to finish, whatever ended it. There is no ancestor
    // Run to bound the drain by, so this is how an owner keeps its own Run from
    // returning while a turn is still writing to a database it is about to
    // close.
    internal async Task Settled() =>
        _ = await WaitForDrainResult().ConfigureAwait(false);

    internal Task Compact(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new ForcedCompactionRequest(cancellationToken);

        lock (_drainGate)
        {
            if (_aborted || lifetime.IsCancellationRequested)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            _forcedCompactions.Enqueue(request);
            if (_drainCancellation is not null)
            {
                _wake = true;
            }
            else
            {
                _drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                _state = DrainState.Running;
                Activity.ChangeState(DrainState.Running);
                _drain = Drain(_drainCancellation.Token);
            }
        }

        return AwaitForcedCompaction(request);
    }

    internal async Task Abort(CancellationToken cancellationToken)
    {
        lock (_drainGate)
        {
            _aborted = true;
            _wake = false;
        }

        await Interrupt(cancellationToken).ConfigureAwait(false);
    }

    internal void SetCheckpoint(string title, long assistantSequence, string toolCallId)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            throw new ArgumentException("checkpoint title must not be blank", nameof(title));
        }

        if (assistantSequence <= 0)
        {
            throw new ArgumentException("checkpoint requires a durable assistant tool batch", nameof(assistantSequence));
        }

        _ = eventRepository.RecordCheckpoint(SessionId, title, assistantSequence, toolCallId);
    }

    internal AgentSelection ResolvePolicySelection()
    {
        var selected = Selection();
        return selected with
        {
            SecurityProfile = identity.PolicyLineage.Resolve(selected.SecurityProfile),
        };
    }

    internal AgentPolicyLineage ResolvePolicyLineage() => identity.PolicyLineage;

    internal bool IsIdle() => _state == DrainState.Idle;

    internal bool IsActive()
    {
        lock (_executionGate)
        {
            return _state != DrainState.Idle || (_started && !_execution.IsCompleted);
        }
    }

    internal bool IsWaitingForIncomingInput()
    {
        lock (_drainGate)
        {
            return _incomingInputWait is not null;
        }
    }

    internal async Task<IncomingActivity?> WaitForIncomingInput(
        TimeSpan duration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = new TaskCompletionSource<IncomingActivity>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_drainGate)
        {
            if (_incomingInputWait is not null)
            {
                throw new InvalidOperationException("An incoming-input wait is already active.");
            }

            _incomingInputWait = incoming;
        }

        if (eventRepository.HasPendingInputs(SessionId))
        {
            _ = incoming.TrySetResult(new IncomingActivity(IncomingActivityKind.Input, string.Empty));
        }

        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(duration, timeProvider, wait.Token);
            Task<bool>? delivery = null;

            if (!incoming.Task.IsCompleted)
            {
                delivery = queues.Deliver(wait.Token);
                var first = await Task.WhenAny(incoming.Task, delay, delivery).ConfigureAwait(false);

                if (first == delivery)
                {
                    _ = await delivery.ConfigureAwait(false);
                }
            }

            if (!incoming.Task.IsCompleted && !delay.IsCompleted)
            {
                _ = await Task.WhenAny(incoming.Task, delay).ConfigureAwait(false);
            }

            var activity = incoming.Task.IsCompletedSuccessfully
                ? await incoming.Task.ConfigureAwait(false)
                : null;
            await wait.CancelAsync().ConfigureAwait(false);

            if (delivery is not null && !delivery.IsCompleted)
            {
                try
                {
                    _ = await delivery.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                }
            }

            cancellationToken.ThrowIfCancellationRequested();
            return activity;
        }
        finally
        {
            lock (_drainGate)
            {
                if (ReferenceEquals(_incomingInputWait, incoming))
                {
                    _incomingInputWait = null;
                }
            }
        }
    }

    internal async Task<(Admission Admission, bool FollowUp)> Send(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        Delivery delivery,
        CancellationToken cancellationToken)
    {
        var admitted = await AdmitPartsAndWake(
            parts,
            messageId,
            delivery,
            new IncomingActivity(IncomingActivityKind.Input, string.Empty),
            cancellationToken).ConfigureAwait(false);
        return (admitted.Admission, admitted.FollowUp);
    }

    internal async Task SetGoal(string goal, CancellationToken cancellationToken)
    {
        var reminder = promptTemplates.Render(
            "goal.root-reminder",
            [new PromptTemplateArgument("goal", goal)]);
        exitReminder.Set(reminder);
        var notice = promptTemplates.Render(
            "goal.root-reminder-notice",
            [new PromptTemplateArgument("reminder", reminder)]);
        _ = await Send(
            [ConversationPart.TextPart(notice)],
            Identifier.MessageId(),
            Delivery.Steer,
            cancellationToken).ConfigureAwait(false);
    }

    internal void ClearGoal() => exitReminder.Set(null);

    internal async Task<bool> ReceiveQueueNotification(
        QueueNotification notification,
        CancellationToken cancellationToken)
    {
        var content = $"Queue notification from \"{notification.Name}\":\n\n{notification.Item}";
        var admission = eventRepository.AdmitSteerIfIdle(
            SessionId,
            notification.Id,
            content,
            input => new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                InputAdmitted = new InputAdmitted
                {
                    InputId = input.Id,
                    MessageId = input.MessageId,
                    Content = input.Content,
                    Delivery = input.Delivery,
                },
            });

        if (admission is null)
        {
            return false;
        }

        if (admission.Published is not null)
        {
            await eventBroker.Publish(admission.Published, cancellationToken).ConfigureAwait(false);
        }

        if (admission.Created || eventRepository.HasPendingInputs(SessionId))
        {
            _ = Wake(new IncomingActivity(IncomingActivityKind.Input, string.Empty));
        }

        return true;
    }

    internal async Task<AgentSendResult> Send(string message, CancellationToken cancellationToken)
    {
        if (lifetime.IsCancellationRequested)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new AgentRegistryException("no message given");
        }

        if (Encoding.UTF8.GetByteCount(message) > MaxAgentMessageBytes)
        {
            throw new AgentRegistryException("agent message exceeds 1048576 bytes");
        }

        var messageId = Identifier.MessageId();
        Task<AgentExecution>? started = null;
        var followUp = false;

        lock (_executionGate)
        {
            if (!_started || _execution.IsCompleted)
            {
                followUp = _started;
                _started = true;
                started = Execute(
                    message,
                    messageId,
                    selectedDrain: null,
                    followUp ? cancellationToken : CancellationToken.None);
                _execution = started;
            }
        }

        if (started is null)
        {
            _ = await Send(
                [ConversationPart.TextPart(message)], messageId, Delivery.Steer, cancellationToken).ConfigureAwait(false);
        }

        return new AgentSendResult(SessionId, Name, messageId, followUp);
    }

    internal async Task ReceiveChildQuestion(
        string message,
        string messageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var admitted = await AdmitPartsAndWake(
            [ConversationPart.TextPart(message)],
            messageId,
            Delivery.Steer,
            new IncomingActivity(IncomingActivityKind.Input, string.Empty),
            cancellationToken).ConfigureAwait(false);

        if (ParentSessionId.Length == 0 || !admitted.FollowUp)
        {
            return;
        }

        lock (_executionGate)
        {
            _started = true;
            _execution = Execute(message, messageId, admitted.SelectedDrain, cancellationToken);
        }
    }

    internal async Task ReceiveAgentCompletion(
        string name,
        string message,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(message);

        var messageId = Identifier.MessageId();
        var admitted = await AdmitPartsAndWake(
            [ConversationPart.TextPart(message)],
            messageId,
            Delivery.Steer,
            new IncomingActivity(IncomingActivityKind.AgentCompletion, name),
            cancellationToken).ConfigureAwait(false);

        if (ParentSessionId.Length == 0 || !admitted.FollowUp)
        {
            return;
        }

        lock (_executionGate)
        {
            _started = true;
            _execution = Execute(message, messageId, admitted.SelectedDrain, cancellationToken);
        }
    }

    internal async Task ReceiveProcessCompletion(
        string name,
        string message,
        string messageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(message);

        _ = await AdmitPartsAndWake(
            [ConversationPart.TextPart(message)],
            messageId,
            Delivery.Steer,
            new IncomingActivity(IncomingActivityKind.ProcessCompletion, name),
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<WaitAgentResult> Wait(
        int yieldAfterMilliseconds,
        CancellationToken cancellationToken)
    {
        Task<AgentExecution> execution;

        lock (_executionGate)
        {
            if (!_started)
            {
                throw new AgentRegistryException("agent has not started");
            }

            execution = _execution;
        }

        var started = Stopwatch.GetTimestamp();

        if (yieldAfterMilliseconds == 0)
        {
            return Terminal(
                await execution.WaitAsync(cancellationToken).ConfigureAwait(false),
                Elapsed(started));
        }

        using var yielded = new CancellationTokenSource(TimeSpan.FromMilliseconds(yieldAfterMilliseconds));
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, yielded.Token);

        try
        {
            return Terminal(await execution.WaitAsync(wait.Token).ConfigureAwait(false), Elapsed(started));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TaskResult(
                AgentTaskStatus.Running,
                yielded: true,
                Elapsed(started),
                string.Empty,
                string.Empty);
        }
    }

    private static long Elapsed(long started) =>
        (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    private static AgentExecution BoundResult(AgentExecution execution) =>
        execution with
        {
            Output = BoundResult(execution.Output),
            Error = BoundResult(execution.Error),
        };

    private static string BoundResult(string value)
    {
        if (Encoding.UTF8.GetByteCount(value) <= MaxAgentResultBytes)
        {
            return value;
        }

        var characters = 0;
        var bytes = 0;

        foreach (var rune in value.EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxAgentResultBytes)
            {
                break;
            }

            bytes += rune.Utf8SequenceLength;
            characters += rune.Utf16SequenceLength;
        }

        return value[..characters];
    }

    private static List<LLMMessage> RestoreHistory(EventRepository repository, string agentSessionId)
    {
        var context = repository.CompactionHistory(agentSessionId);
        if (context is null)
        {
            return [.. repository.Conversation(agentSessionId).Select(item => RestoreMessage(repository, item))];
        }

        var history = new List<LLMMessage> { LLMMessage.System(context.Snapshot.Summary) };
        if (context.Status is not null)
        {
            history.Add(RestoreMessage(repository, context.Status));
        }

        history.AddRange(context.Tail.Select(item => RestoreMessage(repository, item)));
        return history;
    }

    private static LLMMessage RestoreMessage(EventRepository repository, ConversationItem item) => new()
    {
        Role = item.Role,
        Contents = repository.Materialize(item.Parts),
        ToolCalls = item.ToolCalls,
        ToolCallId = item.ToolCallId,
    };

    private static MessageContentPart ToProtocol(ConversationPart part) => part.Kind switch
    {
        ConversationPartKind.Text => new MessageContentPart { Text = part.Text },
        ConversationPartKind.ImageArtifact => new MessageContentPart { ArtifactId = part.ArtifactId },
        _ => throw new InvalidOperationException($"unsupported conversation part {part.Kind}"),
    };

    private static bool IsParallelSafe(ToolSnapshot snapshot, long assistantSequence, LLMToolCall call)
    {
        var tool = snapshot.Find(call.Name);
        return tool is not null
            && tool.IsParallelSafe(new ToolInvocation(call.Id, call.ArgumentsJson, assistantSequence));
    }

    private static async Task AwaitForcedCompaction(ForcedCompactionRequest request) =>
        await request.Completion.Task.WaitAsync(request.CancellationToken).ConfigureAwait(false);

    private AgentSelection CaptureSelection()
    {
        var selected = ResolvePolicySelection();
        return selected with { SecurityProfile = security.Capture(selected.SecurityProfile) };
    }

    private async Task<AgentExecution> WaitForDrainResult()
    {
        Task<AgentExecution> draining;

        lock (_drainGate)
        {
            draining = _drain;
        }

        return await draining.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private Event TranslateProviderEvent(LLMEvent llmEvent)
    {
        var published = new Event { Id = Identifier.EventId(), AgentSessionId = SessionId };

        switch (llmEvent.Kind)
        {
            case LLMEventKind.TextDelta:
                published.TextChunk = new TextChunk { Fragment = llmEvent.Text };
                break;

            case LLMEventKind.ReasoningDelta:
                published.ReasoningChunk = new ReasoningChunk
                {
                    Fragment = llmEvent.Text,
                    Kind = llmEvent.ReasoningKind == LLMReasoningKind.Summary
                        ? ReasoningKind.Summary
                        : ReasoningKind.Raw,
                    PartId = llmEvent.ReasoningPartId,
                    Completed = llmEvent.ReasoningCompleted,
                };
                break;

            case LLMEventKind.ToolCallDelta:
                published.ToolCallChunk = new ToolCallChunk
                {
                    ToolCallId = llmEvent.ToolCallId,
                    ToolName = llmEvent.ToolName,
                    ArgumentsFragment = llmEvent.Text,
                };
                break;

            default:
                published.RetryNotice = new RetryNotice
                {
                    Attempt = llmEvent.Attempt,
                    RetryAfterMs = (int)llmEvent.RetryAfter.TotalMilliseconds,
                    Reason = llmEvent.Text,
                };
                break;
        }

        return published;
    }

    private WaitAgentResult Terminal(AgentExecution completed, long elapsedMilliseconds) =>
        completed.Status switch
        {
            AgentExecutionStatus.Succeeded => TaskResult(
                AgentTaskStatus.Succeeded,
                yielded: false,
                elapsedMilliseconds,
                completed.Output,
                completed.Error),
            AgentExecutionStatus.Failed => TaskResult(
                AgentTaskStatus.Failed,
                yielded: false,
                elapsedMilliseconds,
                completed.Output,
                completed.Error),
            _ => TaskResult(
                AgentTaskStatus.Canceled,
                yielded: false,
                elapsedMilliseconds,
                completed.Output,
                completed.Error),
        };

    private WaitAgentResult TaskResult(
        AgentTaskStatus status,
        bool yielded,
        long elapsedMilliseconds,
        string output,
        string error) =>
        new(SessionId, Name, status, yielded, elapsedMilliseconds, output, error);

    private async Task<AgentExecution> Execute(
        string prompt,
        string messageId,
        Task<AgentExecution>? selectedDrain,
        CancellationToken cancellationToken)
    {
        var activityExecution = Activity.BeginExecution();
        await Task.Yield();

        AgentExecution completed;
        var started = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            AgentStarted = new AgentStarted
            {
                ParentAgentSessionId = identity.ParentSessionId,
                Name = Name,
            },
        };

        try
        {
            await EmitEvent(started, null, null, CancellationToken.None).ConfigureAwait(false);
            if (selectedDrain is null)
            {
                _ = await Send(
                    [ConversationPart.TextPart(prompt)], messageId, Delivery.Steer, cancellationToken).ConfigureAwait(false);
                completed = BoundResult(await WaitForDrainResult().ConfigureAwait(false));
            }
            else
            {
                completed = BoundResult(await selectedDrain.WaitAsync(CancellationToken.None).ConfigureAwait(false));
            }
        }
        catch (Exception failure)
        {
            completed = AgentExecution.Failed(BoundResult(failure.Message));
        }

        ChildQuestionCompletionAttempt terminalCompletionAttempt;
        while (true)
        {
            terminalCompletionAttempt = childQuestions.BeginCompletion();
            if (terminalCompletionAttempt.Reminder is null)
            {
                break;
            }

            terminalCompletionAttempt.Dispose();
            completed = BoundResult(await WaitForDrainResult().ConfigureAwait(false));
        }

        using (terminalCompletionAttempt)
        {
            var terminal = new Event { Id = Identifier.EventId(), AgentSessionId = SessionId };
            if (completed.Status == AgentExecutionStatus.Succeeded)
            {
                terminal.AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = identity.ParentSessionId,
                    Name = Name,
                };
            }
            else
            {
                terminal.AgentFailed = new AgentFailed
                {
                    ParentAgentSessionId = identity.ParentSessionId,
                    Name = Name,
                    Message = completed.Error,
                };
            }

            try
            {
                await EmitEvent(terminal, null, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                completed = AgentExecution.Failed(BoundResult(failure.Message));
            }
        }

        Activity.FinishExecution(activityExecution, completed);
        if (parentScope.Parent is { } parent)
        {
            await parent.ChildRegistry.ReceiveCompletion(identity, completed).ConfigureAwait(false);
        }

        return completed;
    }

    private async Task<(Admission Admission, bool FollowUp, Task<AgentExecution> SelectedDrain)> AdmitPartsAndWake(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        Delivery delivery,
        IncomingActivity activity,
        CancellationToken cancellationToken)
    {
        var admission = eventRepository.Admit(
            SessionId,
            messageId,
            parts,
            delivery,
            input =>
            {
                var admitted = new InputAdmitted
                {
                    InputId = input.Id,
                    MessageId = input.MessageId,
                    Content = input.Content,
                    Delivery = input.Delivery,
                };
                admitted.Parts.AddRange(input.Parts.Select(ToProtocol));
                return new Event
                {
                    Id = Identifier.EventId(),
                    AgentSessionId = SessionId,
                    InputAdmitted = admitted,
                };
            });

        // Only a real admission has an event; a re-send of one already taken
        // has nothing new to publish, but still wakes, because the sender
        // re-sent precisely because they were not sure it had been.
        if (admission.Published is not null)
        {
            await eventBroker.Publish(admission.Published, cancellationToken).ConfigureAwait(false);
        }

        var incoming = admission.Created || eventRepository.HasPendingInputs(SessionId) ? activity : null;
        var (followUp, selectedDrain) = WakeSelected(incoming);
        return (admission, followUp, selectedDrain);
    }

    // Starts a drain, or tells the one already running that there is more to
    // take. Coalescing rather than starting a second drain is what keeps
    // principle 2: one owner, however many prompts arrive.
    private bool Wake(IncomingActivity? activity) => WakeSelected(activity).FollowUp;

    private (bool FollowUp, Task<AgentExecution> SelectedDrain) WakeSelected(IncomingActivity? activity)
    {
        lock (_drainGate)
        {
            if (_aborted)
            {
                return (false, _drain);
            }

            if (activity is not null)
            {
                _ = _incomingInputWait?.TrySetResult(activity);
            }

            if (_drainCancellation is not null)
            {
                _wake = true;
                return (false, _drain);
            }

            // Linked to the session's lifetime, never to the request that woke
            // it: a unary call's token is cancelled when the call returns, and
            // the turn outlives the call that admitted its prompt.
            _drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            _state = DrainState.Running;
            Activity.ChangeState(DrainState.Running);
            _drain = Drain(_drainCancellation.Token);
            return (true, _drain);
        }
    }

    private async Task<AgentExecution> Drain(CancellationToken cancellationToken)
    {
        // The drain belongs to the session, not to whoever admitted the prompt:
        // yielding here returns Wake to its caller instead of running the first
        // turn on the admitting thread.
        await Task.Yield();

        var completed = AgentExecution.Succeeded(string.Empty);

        while (true)
        {
            await RunForcedCompactions(cancellationToken).ConfigureAwait(false);
            var pass = await Pass(turnOpen: false, null, cancellationToken).ConfigureAwait(false);
            if (pass.Status != AgentExecutionStatus.Succeeded || pass.Output.Length > 0)
            {
                completed = pass;
            }

            lock (_drainGate)
            {
                // Admitted after the last promotion looked and before the drain
                // settled. Check durable input as well as the in-memory wake so
                // recovered or otherwise pre-existing input cannot be stranded.
                if (pass.Status == AgentExecutionStatus.Succeeded
                    && !cancellationToken.IsCancellationRequested
                    && (_forcedCompactions.Count > 0 || _wake || eventRepository.HasPendingInputs(SessionId)))
                {
                    _wake = false;
                    continue;
                }

                // Left alone while an interrupt is unwinding: that caller is
                // still holding it, and disposes it once this task has ended.
                if (!_stopping)
                {
                    _drainCancellation?.Dispose();
                }

                _drainCancellation = null;
                while (_forcedCompactions.TryDequeue(out var forcedCompaction))
                {
                    _ = forcedCompaction.Completion.TrySetCanceled(cancellationToken);
                }

                _state = DrainState.Idle;
                Activity.ChangeState(DrainState.Idle);
            }

            if (completed.Status == AgentExecutionStatus.Succeeded
                && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _ = await queues.Deliver(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                }
            }

            return completed;
        }
    }

    // One pass: promote what is due, call the provider, run what it asks for,
    // and repeat until nothing is left to answer. The sequence is the one
    // docs/architecture.md fixes, and its two promotion points are the whole
    // difference between a steer and a queued prompt.
    private async Task<AgentExecution> Pass(
        bool turnOpen,
        AgentTurnSelection? activeSelection,
        CancellationToken cancellationToken)
    {
        var answer = string.Empty;
        var providerRequests = 0;
        var completionRetryPending = false;
        ToolSnapshot? activeTools = null;

        try
        {
            while (true)
            {
                if (!turnOpen && HasForcedCompactions())
                {
                    return AgentExecution.Succeeded(answer);
                }

                await ReconcileToolBatchesUsingCurrentConfiguration(cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                // Looking is not consuming. A pending status remains pending
                // while an idle drain has no input that could reach a provider.
                if (!completionRetryPending && !Answerable() && !eventRepository.HasPendingInputs(SessionId))
                {
                    return AgentExecution.Succeeded(answer);
                }

                if (!turnOpen)
                {
                    var captured = CaptureSelection();
                    captured.Profile.Prepare();
                    var resolved = router.Resolve(captured.RequestedModel.Value);
                    activeSelection = new AgentTurnSelection(
                        resolved.RequestedSelector,
                        resolved,
                        captured.Profile,
                        captured.SecurityProfile);
                    providerRequests = 0;
                    turnOpen = true;
                    var aliasIcon = resolved.Alias?.Icon;
                    var started = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                        TurnStarted = new TurnStarted
                        {
                            Model = resolved.CanonicalModel.Selector,
                            ModelAliasIcon = aliasIcon is null
                                ? null
                                : new TurnModelAliasIcon
                                {
                                    Glyph = aliasIcon.Glyph,
                                    Color = aliasIcon.Color switch
                                    {
                                        ModelAliasIconColor.Black => TurnModelAliasIconColor.Black,
                                        ModelAliasIconColor.Red => TurnModelAliasIconColor.Red,
                                        ModelAliasIconColor.Green => TurnModelAliasIconColor.Green,
                                        ModelAliasIconColor.Yellow => TurnModelAliasIconColor.Yellow,
                                        ModelAliasIconColor.Blue => TurnModelAliasIconColor.Blue,
                                        ModelAliasIconColor.Magenta => TurnModelAliasIconColor.Magenta,
                                        ModelAliasIconColor.Cyan => TurnModelAliasIconColor.Cyan,
                                        ModelAliasIconColor.White => TurnModelAliasIconColor.White,
                                        ModelAliasIconColor.Gray => TurnModelAliasIconColor.Gray,
                                        _ => throw new InvalidOperationException("The model alias icon color is invalid."),
                                    },
                                },
                        },
                    };
                    await EmitEvent(started, null, null, cancellationToken).ConfigureAwait(false);
                    activeSelection = await InjectStatus(activeSelection, cancellationToken).ConfigureAwait(false);
                    activeTools = MaterializeTools()
                        .Without(activeSelection.Profile.DisabledTools)
                        .Only(activeSelection.Profile.AllowedTools);
                    await RestoreToolAvailability(cancellationToken).ConfigureAwait(false);
                }

                // Status is committed before promotion, so sequenced history is
                // epoch baseline, status, then the user input it describes.
                _ = await Promote(cancellationToken).ConfigureAwait(false);

                if (activeSelection is null || activeTools is null)
                {
                    throw new AgentRegistryException("turn selection is unavailable");
                }

                var maxTurns = activeSelection.Profile.MaxTurns;
                if (providerRequests >= maxTurns)
                {
                    await Fail(_runawayMessage, string.Empty, cancellationToken).ConfigureAwait(false);
                    return AgentExecution.Failed(_runawayMessage);
                }

                var finalProviderRequest = providerRequests + 1 == maxTurns;
                var snapshot = finalProviderRequest
                    ? ToolSnapshot.Empty
                    : activeTools;
                if (finalProviderRequest)
                {
                    await InjectFinalProviderRequestPrompt(cancellationToken).ConfigureAwait(false);
                }

                var instructions = await PrepareEpoch(
                    activeSelection,
                    snapshot.Definitions,
                    cancellationToken).ConfigureAwait(false);
                var messages = new List<LLMMessage>(_history);

                providerRequests++;
                var completed = await Call(activeSelection, snapshot, instructions, messages, cancellationToken)
                    .ConfigureAwait(false);

                if (completed.ToolCalls.Count > 0)
                {
                    var published = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                    };
                    eventRepository.AppendConversation(
                        published,
                        ConversationOrigin.Model,
                        LLMRole.Assistant,
                        [ConversationPart.TextPart(completed.AssistantText)],
                        completed.ToolCalls,
                        string.Empty);
                    _history.Add(LLMMessage.Assistant(completed.AssistantText, completed.ToolCalls));
                    await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
                    Activity.RecordAssistantMessage(completed.AssistantText);
                    await ReconcileToolBatches(
                        activeSelection,
                        snapshot,
                        cancellationToken).ConfigureAwait(false);

                    if (providerRequests == maxTurns)
                    {
                        await Fail(_runawayMessage, string.Empty, cancellationToken).ConfigureAwait(false);
                        return AgentExecution.Failed(_runawayMessage);
                    }

                    continue;
                }

                var completionCandidate = new AgentTurnCompletionCandidate(
                    SessionId,
                    Identifier.MessageId(),
                    completed.AssistantText,
                    activeSelection.Profile);
                List<IDisposable> completionReservations = [];
                PlanCompleted? deferredPlanCompletion = null;
                AgentTurnCompletionOutcome.RetryOutcome? retryOutcome = null;
                try
                {
                    foreach (var callback in _turnCompletionCallbacks)
                    {
                        var outcome = await callback.Complete(completionCandidate, cancellationToken)
                            .ConfigureAwait(false);
                        switch (outcome)
                        {
                            case AgentTurnCompletionOutcome.ContinueOutcome continuation:
                                if (continuation.CompletionReservation is { } completionReservation)
                                {
                                    completionReservations.Add(completionReservation);
                                }

                                if (continuation.DeferredPlanCompletion is { } planCompletion)
                                {
                                    deferredPlanCompletion = planCompletion;
                                }

                                break;
                            case AgentTurnCompletionOutcome.RetryOutcome retry:
                                retryOutcome = retry;
                                break;
                        }

                        if (retryOutcome is not null)
                        {
                            break;
                        }
                    }

                    if (retryOutcome is not null)
                    {
                        await ApplyCompletionRetry(retryOutcome, completed.AssistantText, cancellationToken)
                            .ConfigureAwait(false);
                        if (retryOutcome.SelectCandidateAnswer)
                        {
                            answer = completed.AssistantText;
                        }

                        if (retryOutcome.CompletionRetryPending is { } retryPending)
                        {
                            completionRetryPending = retryPending;
                        }

                        if (retryOutcome.ResetProviderRequestBudget)
                        {
                            providerRequests = 0;
                        }

                        continue;
                    }

                    answer = completed.AssistantText;
                    completionRetryPending = false;
                    _history.Add(LLMMessage.Assistant(completed.AssistantText, []));
                    var ended = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                        TurnEnded = new TurnEnded
                        {
                            FinishReason = completed.FinishReason,
                            InputTokens = _statistics.InputTokens,
                            OutputTokens = _statistics.OutputTokens,
                        },
                    };
                    if (deferredPlanCompletion is { } planCompleted)
                    {
                        var plan = new Event
                        {
                            Id = Identifier.EventId(),
                            AgentSessionId = SessionId,
                            PlanCompleted = planCompleted,
                        };
                        await EmitEvent(plan, null, null, cancellationToken).ConfigureAwait(false);
                    }

                    await EmitEvent(ended, "assistant", completed.AssistantText, cancellationToken)
                        .ConfigureAwait(false);
                    Activity.RecordAssistantMessage(completed.AssistantText);

                    // Back to the top rather than out: a queued prompt is promoted
                    // exactly here, where the turn would otherwise stop.
                    turnOpen = false;
                    activeSelection = null;
                    activeTools = null;
                }
                finally
                {
                    foreach (var completionReservation in completionReservations)
                    {
                        completionReservation.Dispose();
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Not a failure: the turn was stopped, and it stopped with every
            // tool call settled. Reported on the same event as any other
            // ending, because it is one.
            if (turnOpen)
            {
                _history.Add(LLMMessage.Assistant(_interruptedNote, []));

                var ended = new Event
                {
                    Id = Identifier.EventId(),
                    AgentSessionId = SessionId,
                    TurnEnded = new TurnEnded
                    {
                        FinishReason = InterruptedFinish,
                        InputTokens = _statistics.InputTokens,
                        OutputTokens = _statistics.OutputTokens,
                    },
                };
                await EmitEvent(ended, "assistant", _interruptedNote, CancellationToken.None)
                    .ConfigureAwait(false);
                Activity.RecordAssistantMessage(_interruptedNote);
            }

            return AgentExecution.Canceled();
        }
        catch (Exception failure)
        {
            // A provider or tool boundary is a deliberate containment point, and
            // this one is total: the drain is nobody's awaited task, so an
            // escaping exception would be unobserved rather than reported.
            await Fail(
                failure.Message,
                ProviderErrors.ReadResponseBody(failure),
                CancellationToken.None).ConfigureAwait(false);
            return AgentExecution.Failed(failure.Message);
        }
    }

    private async Task ApplyCompletionRetry(
        AgentTurnCompletionOutcome.RetryOutcome outcome,
        string assistantText,
        CancellationToken cancellationToken)
    {
        var systemMessage = outcome.SystemMessage
            ?? throw new InvalidOperationException("A retry outcome requires a system message.");
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
        };
        switch (outcome.Projection)
        {
            case AgentTurnCompletionProjection.PendingChildQuestionReminder:
                eventRepository.AppendPendingChildQuestionReminder(published, assistantText, systemMessage);
                break;
            case AgentTurnCompletionProjection.ActiveWorkReminder:
                eventRepository.AppendActiveWorkReminder(published, systemMessage);
                break;
            case AgentTurnCompletionProjection.PlanValidationRepair:
                published.PlanValidationRepairInjected = new PlanValidationRepairInjected
                {
                    Diagnostic = systemMessage,
                };
                eventRepository.AppendPlanValidationRepair(published, assistantText, systemMessage);
                break;
            case AgentTurnCompletionProjection.ExitReminder:
                eventRepository.AppendExitReminder(published, assistantText, systemMessage);
                break;
            default:
                throw new InvalidOperationException("Unknown completion retry projection.");
        }

        if (outcome.RetainCandidateAssistant)
        {
            _history.Add(LLMMessage.Assistant(assistantText, []));
        }

        _history.Add(LLMMessage.System(systemMessage));
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        if (outcome.RecordAssistantActivity)
        {
            Activity.RecordAssistantMessage(assistantText);
        }
    }

    // Steers first, all of them: they join the turn already running. A queued
    // prompt is taken only when nothing else is owed an answer, which is what
    // makes it a turn of its own rather than a second voice in this one.
    private async Task<int> Promote(CancellationToken cancellationToken)
    {
        var promoted = eventRepository.PromoteSteers(
            SessionId,
            input => new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                InputPromoted = new InputPromoted { InputId = input.Id, MessageId = input.MessageId },
            });

        if (promoted.Count == 0 && !Answerable())
        {
            promoted = eventRepository.PromoteNextQueue(
                SessionId,
                input => new Event
                {
                    Id = Identifier.EventId(),
                    AgentSessionId = SessionId,
                    InputPromoted = new InputPromoted { InputId = input.Id, MessageId = input.MessageId },
                });
        }

        foreach (var promotion in promoted)
        {
            _history.Add(LLMMessage.User(eventRepository.Materialize(promotion.Input.Parts)));
            await eventBroker.Publish(promotion.Published, cancellationToken).ConfigureAwait(false);
        }

        return promoted.Count;
    }

    // Whether the model owes an answer. A history ending in a user prompt or a
    // tool result is unanswered; one ending in an assistant message is not.
    private bool Answerable() =>
        _history.LastOrDefault(message => message.Role != LLMRole.System)?.Role is LLMRole.User or LLMRole.Tool;

    // Every call the model made gets a result, even when the turn is stopped
    // part-way through: a provider rejects a history holding a call with no
    // answer, so an interrupt that left one behind would break every later
    // prompt rather than only this turn (principle 6).
    private ToolSnapshot MaterializeTools()
    {
        if (_tools is not null)
        {
            return _tools;
        }

        var tools = new List<ITool>(toolFactories.Count);
        var supported = new List<bool>(toolFactories.Count);
        foreach (var factory in toolFactories)
        {
            supported.Add(factory.Supports(this));
            tools.Add(factory.Create(this));
        }

        _tools = ToolSnapshot.Document(tools, supported, toolDefinitions);
        return _tools;
    }

    private async Task ReconcileToolBatchesUsingCurrentConfiguration(CancellationToken cancellationToken)
    {
        var captured = CaptureSelection();
        var resolved = router.Resolve(captured.RequestedModel.Value);
        var selection = new AgentTurnSelection(
            resolved.RequestedSelector,
            resolved,
            captured.Profile,
            captured.SecurityProfile);
        var tools = MaterializeTools()
            .Without(captured.Profile.DisabledTools)
            .Only(captured.Profile.AllowedTools);
        await ReconcileToolBatches(selection, tools, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReconcileToolBatches(
        AgentTurnSelection selection,
        ToolSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var conversation = eventRepository.Conversation(SessionId);
        var terminals = eventRepository.ToolTerminals(SessionId)
            .ToDictionary(terminal => terminal.ToolCallId, StringComparer.Ordinal);
        var changed = false;

        foreach (var batch in conversation.Where(item => item.Role == LLMRole.Assistant && item.ToolCalls.Count > 0))
        {
            var stopped = false;
            var callIndex = 0;
            while (callIndex < batch.ToolCalls.Count)
            {
                var call = batch.ToolCalls[callIndex];
                if (terminals.TryGetValue(call.Id, out var restoredTerminal))
                {
                    changed |= eventRepository.AppendToolSettlement(
                        new Event { Id = Identifier.EventId(), AgentSessionId = SessionId },
                        batch.Sequence,
                        restoredTerminal);
                    stopped |= restoredTerminal.Status == ToolExecutionStatus.Cancelled;
                    callIndex++;
                    continue;
                }

                if (stopped || cancellationToken.IsCancellationRequested)
                {
                    var cancelled = CancelTool(call);
                    await SettleTool(batch.Sequence, cancelled, terminals).ConfigureAwait(false);
                    changed = true;
                    stopped = true;
                    callIndex++;
                    continue;
                }

                if (!IsParallelSafe(snapshot, batch.Sequence, call))
                {
                    var settlement = await Invoke(selection, snapshot, batch.Sequence, call, cancellationToken)
                        .ConfigureAwait(false);
                    await SettleTool(batch.Sequence, settlement, terminals).ConfigureAwait(false);
                    changed = true;
                    stopped |= settlement.Terminal.Status == ToolExecutionStatus.Cancelled;
                    callIndex++;
                    continue;
                }

                var runEnd = callIndex;
                var executions = new Dictionary<string, Task<(Event Published, ToolExecutionTerminal Terminal)>>(
                    StringComparer.Ordinal);
                while (runEnd < batch.ToolCalls.Count)
                {
                    var candidate = batch.ToolCalls[runEnd];
                    if (!IsParallelSafe(snapshot, batch.Sequence, candidate))
                    {
                        break;
                    }

                    if (terminals.TryGetValue(candidate.Id, out var candidateTerminal))
                    {
                        if (candidateTerminal.Status == ToolExecutionStatus.Cancelled)
                        {
                            break;
                        }

                        runEnd++;
                        continue;
                    }

                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    executions.Add(
                        candidate.Id,
                        Invoke(selection, snapshot, batch.Sequence, candidate, cancellationToken));
                    runEnd++;
                }

                if (runEnd == callIndex)
                {
                    continue;
                }

                var settlements = await Task.WhenAll(executions.Values).ConfigureAwait(false);
                var settlementsByCall = settlements.ToDictionary(
                    settlement => settlement.Terminal.ToolCallId,
                    StringComparer.Ordinal);
                while (callIndex < runEnd)
                {
                    call = batch.ToolCalls[callIndex];
                    if (terminals.TryGetValue(call.Id, out restoredTerminal))
                    {
                        changed |= eventRepository.AppendToolSettlement(
                            new Event { Id = Identifier.EventId(), AgentSessionId = SessionId },
                            batch.Sequence,
                            restoredTerminal);
                    }
                    else
                    {
                        var settlement = settlementsByCall[call.Id];
                        await SettleTool(batch.Sequence, settlement, terminals).ConfigureAwait(false);
                        changed = true;
                        stopped |= settlement.Terminal.Status == ToolExecutionStatus.Cancelled;
                    }

                    callIndex++;
                }
            }

            var images = batch.ToolCalls
                .Select(call => terminals[call.Id])
                .Where(terminal => terminal.Status == ToolExecutionStatus.Finished)
                .SelectMany(terminal => terminal.ResultParts)
                .Where(part => part.Kind == ConversationPartKind.ImageArtifact)
                .ToArray();
            if (images.Length > 0 && !eventRepository.HasToolSynthetic(batch.Sequence, SessionId))
            {
                var published = new Event { Id = Identifier.EventId(), AgentSessionId = SessionId };
                _ = eventRepository.AppendToolSynthetic(published, batch.Sequence, images);
                await eventBroker.Publish(published, CancellationToken.None).ConfigureAwait(false);
                changed = true;
            }
        }

        if (changed)
        {
            _history.Clear();
            _history.AddRange(RestoreHistory(eventRepository, SessionId));
        }
    }

    private async Task SettleTool(
        long assistantSequence,
        (Event Published, ToolExecutionTerminal Terminal) settlement,
        Dictionary<string, ToolExecutionTerminal> terminals)
    {
        _ = eventRepository.AppendToolSettlement(settlement.Published, assistantSequence, settlement.Terminal);
        await eventBroker.Publish(settlement.Published, CancellationToken.None).ConfigureAwait(false);
        terminals.Add(settlement.Terminal.ToolCallId, settlement.Terminal);
    }

    private bool HasForcedCompactions()
    {
        lock (_drainGate)
        {
            return _forcedCompactions.Count > 0;
        }
    }

    private async Task RunForcedCompactions(CancellationToken cancellationToken)
    {
        while (true)
        {
            ForcedCompactionRequest? request;
            lock (_drainGate)
            {
                _ = _forcedCompactions.TryDequeue(out request);
            }

            if (request is null)
            {
                return;
            }

            try
            {
                using var operation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    request.CancellationToken);
                operation.Token.ThrowIfCancellationRequested();
                await ReconcileToolBatchesUsingCurrentConfiguration(operation.Token).ConfigureAwait(false);
                var captured = CaptureSelection();
                captured.Profile.Prepare();
                var resolved = router.Resolve(captured.RequestedModel.Value);
                var selection = new AgentTurnSelection(
                    resolved.RequestedSelector,
                    resolved,
                    captured.Profile,
                    captured.SecurityProfile);
                var tools = MaterializeTools()
                    .Without(selection.Profile.DisabledTools)
                    .Only(selection.Profile.AllowedTools);
                if (!_epochInitialized)
                {
                    _systemPrompt.RenewEpoch();
                    _epochInitialized = true;
                }

                var instructions = _systemPrompt.Build(selection);
                _ = await CompactEpoch(selection, tools.Definitions, instructions, operation.Token).ConfigureAwait(false);
                _ = request.Completion.TrySetResult();
            }
            catch (OperationCanceledException failure)
            {
                _ = request.Completion.TrySetCanceled(failure.CancellationToken);
            }
            catch (Exception failure)
            {
                _ = request.Completion.TrySetException(failure);
            }
        }
    }

    // Sampled at the start of an epoch, not every turn, and compaction starts a
    // fresh one -- so a turn never begins already over the window.
    private async Task<string> PrepareEpoch(
        AgentTurnSelection selection,
        IReadOnlyList<LLMToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        if (!_epochInitialized)
        {
            _systemPrompt.RenewEpoch();
            _epochInitialized = true;
        }

        var instructions = _systemPrompt.Build(selection);
        if (!compactor.ShouldCompact(selection.ResolvedModel.CanonicalModel, instructions, tools, _history))
        {
            return instructions;
        }

        return await CompactEpoch(selection, tools, instructions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> CompactEpoch(
        AgentTurnSelection selection,
        IReadOnlyList<LLMToolDefinition> tools,
        string instructions,
        CancellationToken cancellationToken)
    {
        var selectedModel = selection.ResolvedModel.CanonicalModel;
        var started = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            CompactionStarted = new CompactionStarted(),
        };
        await EmitEvent(started, null, null, CancellationToken.None).ConfigureAwait(false);

        try
        {
            _systemPrompt.RenewEpoch();
            instructions = _systemPrompt.Build(selection);
            var effective = eventRepository.EffectiveConversationGroups(SessionId);
            var activeCheckpoints = eventRepository.ActiveCheckpointAssistantSequences(SessionId);
            var compactionGroups = new List<CompactionGroup>();
            if (effective.Snapshot is not null)
            {
                compactionGroups.Add(new CompactionGroup(
                    [LLMMessage.System(effective.Snapshot.Summary)],
                    effective.Snapshot.Watermark,
                    false));
            }

            compactionGroups.AddRange(effective.Groups.Select(group => new CompactionGroup(
                [.. group.Items.Select(item => RestoreMessage(eventRepository, item))],
                group.EndWatermark,
                group.AssistantSequence > 0 && activeCheckpoints.Contains(group.AssistantSequence))));
            var statusContent = await status.Observe(this, selection, selection.Profile, cancellationToken)
                .ConfigureAwait(false);
            var fixedStatus = LLMMessage.System(statusContent);
            var compacted = await compactor.Compact(
                selectedModel,
                instructions,
                tools,
                compactionGroups,
                effective.Snapshot?.Watermark ?? 0,
                fixedStatus,
                cancellationToken).ConfigureAwait(false);
            if (compacted is not null
                && compacted.Watermark > (effective.Snapshot?.Watermark ?? 0))
            {
                var statusInjected = new Event
                {
                    Id = Identifier.EventId(),
                    AgentSessionId = SessionId,
                    StatusInjected = new StatusInjected(),
                };
                if (!eventRepository.AppendCompactionStatus(
                        statusInjected,
                        new CompactionSnapshot(compacted.Summary.Content, compacted.Watermark),
                        statusContent))
                {
                    throw new InvalidOperationException("compaction snapshot was already persisted");
                }

                _history.Clear();
                _history.AddRange(RestoreHistory(eventRepository, SessionId));
                await eventBroker.Publish(statusInjected, CancellationToken.None).ConfigureAwait(false);
            }

            var finished = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                CompactionFinished = new CompactionFinished(),
            };
            await EmitEvent(finished, null, null, CancellationToken.None).ConfigureAwait(false);
            return instructions;
        }
        catch (Exception failure)
        {
            var failed = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                CompactionFailed = new CompactionFailed { Message = failure.Message },
            };
            await EmitEvent(failed, null, null, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private async Task InjectFinalProviderRequestPrompt(CancellationToken cancellationToken)
    {
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
        };
        eventRepository.AppendFinalProviderRequestPrompt(published, _finalProviderRequestPrompt);
        _history.Add(LLMMessage.System(_finalProviderRequestPrompt));
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreToolAvailability(CancellationToken cancellationToken)
    {
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
        };
        if (!eventRepository.AppendToolAvailabilityRestoredPrompt(published, _toolAvailabilityRestoredPrompt))
        {
            return;
        }

        _history.Add(LLMMessage.System(_toolAvailabilityRestoredPrompt));
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentTurnSelection> InjectStatus(
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        if (Depth > 0)
        {
            if (!_initialStatusPending)
            {
                return selection;
            }

            var content = await status.Observe(this, selection, selection.Profile, cancellationToken)
                .ConfigureAwait(false);
            var published = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                StatusInjected = new StatusInjected(),
            };
            eventRepository.AppendInitialStatusPrompt(published, content);
            _history.Add(LLMMessage.System(content));
            _initialStatusPending = false;
            await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
            return selection;
        }

        while (eventRepository.PendingStatus(SessionId) is { } pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = selection.Profile;

            if (!string.Equals(profile.Id, pending.Mode, StringComparison.Ordinal))
            {
                selection = RefreshSelection(selection);
                selection.Profile.Prepare();
                await Task.Yield();
                continue;
            }

            var content = await status.Observe(this, selection, profile, cancellationToken).ConfigureAwait(false);
            var published = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                StatusInjected = new StatusInjected(),
            };
            if (!eventRepository.AppendStatusPrompt(published, pending, content))
            {
                selection = RefreshSelection(selection);
                selection.Profile.Prepare();
                continue;
            }

            _history.Add(LLMMessage.System(content));
            await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        }

        return selection;
    }

    private AgentTurnSelection RefreshSelection(AgentTurnSelection active)
    {
        var selected = CaptureSelection();
        return active with
        {
            Profile = selected.Profile,
            SecurityProfile = selected.SecurityProfile,
        };
    }

    // Streams one provider call: deltas go out as events, and the terminal
    // Completed is returned so the loop can decide what to do next.
    private async Task<LLMEvent> Call(
        AgentTurnSelection? selection,
        ToolSnapshot snapshot,
        string instructions,
        IReadOnlyList<LLMMessage> messages,
        CancellationToken cancellationToken)
    {
        var selectedModel = selection?.ResolvedModel.CanonicalModel
            ?? throw new AgentRegistryException("turn selection is unavailable");
        var request = new LLMRequest
        {
            Model = selectedModel.ModelId,
            MaxTokens = 4096,
            Instructions = instructions,
            Messages = messages,
            Tools = snapshot.Definitions,
            Reasoning = selectedModel.Reasoning,
        };

        var completed = LLMEvent.Completed(string.Empty, 0, 0, 0, string.Empty, []);

        try
        {
            Activity.BeginProviderRequest();
            await foreach (var llmEvent in selectedModel.Provider
                .Call(request, cancellationToken).ConfigureAwait(false))
            {
                if (llmEvent.Kind == LLMEventKind.Completed)
                {
                    var statistics = _statistics.Add(llmEvent, selectedModel.Model);
                    var published = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                        AgentStatisticsUpdated = statistics.ConvertToPayload(),
                    };
                    await EmitEvent(published, null, null, CancellationToken.None).ConfigureAwait(false);
                    eventBroker.PublishTransient(
                        new Event
                        {
                            Id = Identifier.EventId(),
                            AgentSessionId = SessionId,
                            ProviderCallUsage = new ProviderCallUsage
                            {
                                InputTokens = Math.Max(0, llmEvent.InputTokens),
                                OutputTokens = Math.Max(0, llmEvent.OutputTokens),
                            },
                        });
                    _statistics = statistics;
                    completed = llmEvent;
                    continue;
                }

                Activity.ObserveProviderEvent(llmEvent);
                await EmitEvent(TranslateProviderEvent(llmEvent), null, null, cancellationToken).ConfigureAwait(false);
            }

            return completed;
        }
        finally
        {
            Activity.FinishProviderRequest();
        }
    }

    private async Task<(Event Published, ToolExecutionTerminal Terminal)> Invoke(
        AgentTurnSelection selection,
        ToolSnapshot snapshot,
        long assistantSequence,
        LLMToolCall call,
        CancellationToken cancellationToken)
    {
        var started = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            ToolStarted = new ToolStarted { ToolCallId = call.Id, ToolName = call.Name },
        };
        await EmitEvent(started, null, null, CancellationToken.None).ConfigureAwait(false);

        var tool = snapshot.Find(call.Name);
        if (tool is null)
        {
            return FailTool(call, $"unknown tool {call.Name}");
        }

        try
        {
            var effective = CaptureSelection();
            var invocationSelection = selection with { SecurityProfile = effective.SecurityProfile };
            var invocation = new ToolInvocation(call.Id, call.ArgumentsJson, assistantSequence)
            {
                PromptTemplates = promptTemplates,
            };
            ToolExecutionResult result;
            var execution = Activity.BeginTool(tool.Name);
            try
            {
                result = await tool.Execute(invocation, invocationSelection, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Activity.FinishTool(execution);
            }

            var text = promptTemplates.Render(
                "tool-result.text",
                [new PromptTemplateArgument("value", result.Text)]);
            if (ToolOutputBlobStore.IsOversized(text))
            {
                text = await toolOutputBlobs.Persist(text, CancellationToken.None).ConfigureAwait(false);
            }

            var terminal = new ToolFinished { ToolCallId = call.Id, ToolName = call.Name, Result = text };
            if (result.YieldedProcess is { } yielded)
            {
                var protocolYielded = new Protocol.YieldedShellProcess
                {
                    ProcessId = yielded.ProcessId,
                    Name = yielded.Name,
                    InventoryInstanceId = yielded.InventoryInstanceId,
                    VisibleRevision = yielded.VisibleRevision,
                };
                if (yielded.StdoutPath is { } stdoutPath)
                {
                    protocolYielded.StdoutPath = stdoutPath;
                }

                if (yielded.StderrPath is { } stderrPath)
                {
                    protocolYielded.StderrPath = stderrPath;
                }

                terminal.YieldedProcess = protocolYielded;
            }

            var finished = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                ToolFinished = terminal,
            };
            var parts = new List<ConversationPart> { ConversationPart.TextPart(text) };
            parts.AddRange(result.ImageArtifacts.Select(ConversationPart.ImageArtifact));
            return (finished, new ToolExecutionTerminal(
                call.Id,
                call.Name,
                ToolExecutionStatus.Finished,
                parts,
                text));
        }
        catch (OperationCanceledException)
        {
            return CancelTool(call);
        }
        catch (Exception failure)
        {
            return FailTool(call, failure.Message);
        }
    }

    private (Event Published, ToolExecutionTerminal Terminal) CancelTool(LLMToolCall call)
    {
        var cancelled = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            ToolCancelled = new ToolCancelled { ToolCallId = call.Id, ToolName = call.Name },
        };
        return (cancelled, new ToolExecutionTerminal(
            call.Id,
            call.Name,
            ToolExecutionStatus.Cancelled,
            [ConversationPart.TextPart(_interruptedResult)],
            _interruptedResult));
    }

    private (Event Published, ToolExecutionTerminal Terminal) FailTool(LLMToolCall call, string message)
    {
        var failed = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            ToolError = new ToolError { ToolCallId = call.Id, ToolName = call.Name, Message = message },
        };
        var result = promptTemplates.Render(
            "tool-result.error",
            [new PromptTemplateArgument("message", message)]);
        return (failed, new ToolExecutionTerminal(
            call.Id,
            call.Name,
            ToolExecutionStatus.Error,
            [ConversationPart.TextPart(result)],
            result));
    }

    private async Task Fail(string message, string providerResponseBody, CancellationToken cancellationToken)
    {
        var failed = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            TurnFailed = new TurnFailed
            {
                Message = message,
                ProviderResponseBody = providerResponseBody,
            },
        };
        await EmitEvent(failed, null, null, cancellationToken).ConfigureAwait(false);
    }

    // Commits the event and its projection, then publishes it. In that order:
    // EventBroker must only ever hand a subscriber an event the repository has
    // already committed, so nothing observable can be un-happened by a crash.
    private async ValueTask EmitEvent(
        Event published,
        string? role,
        string? content,
        CancellationToken cancellationToken)
    {
        var usage = eventRepository.Append(published, role, content);
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        if (usage is not null)
        {
            await eventBroker.Publish(usage.ConvertToEvent(), cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ForcedCompactionRequest(CancellationToken cancellationToken)
    {
        internal CancellationToken CancellationToken { get; } = cancellationToken;

        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

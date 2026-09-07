using System.Diagnostics;
using System.Text;
using Parrot.Config;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Queues;
using Parrot.Skills;
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
internal sealed partial class AgentSession(
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
    CompactionGroupBlobStore compactionGroupBlobs,
    Compactor compactor,
    ProviderSessions providerSessions,
    ContextCadence contextCadence,
    PromptTemplateCatalog promptTemplates,
    ChildQuestionCoordinator childQuestions,
    ExitReminder exitReminder,
    IMode mode,
    [InjectionTag("turnCompletionCallbacks")] IReadOnlyList<IAgentTurnCompletionCallback> turnCompletionCallbacks,
    AgentSessionSecurity security,
    AgentSkills skills,
    RuntimeStatus status,
    AgentQueues queues,
    AgentSessionActivity activity,
    CancellationToken lifetime) : IAgentSession
{
    private const string InterruptedFinish = "interrupted";
    private const int DefaultMaximumOutputTokens = 32 * 1024;
    private const int MaxAgentMessageBytes = 1024 * 1024;
    private const int MaxAgentResultBytes = 1024 * 1024;

    private readonly string _runawayMessage = promptTemplates.Render("agent-session.runaway", []);
    private readonly string _truncatedToolCallPrompt = promptTemplates.Render("agent-session.truncated-tool-call", []);
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

    private readonly DrainLifecycle _drainLifecycle = new();

    private readonly EpochContext _epochContext = new(identity.Depth > 0);

    // Compaction runs inside the drain so it cannot mutate history concurrently
    // with a turn. Callers enqueue requests under the drain gate and wake that drain.
    private readonly Queue<ForcedCompactionRequest> _forcedCompactions = [];
    private readonly IReadOnlyList<IAgentTurnCompletionCallback> _turnCompletionCallbacks =
        turnCompletionCallbacks ?? throw new ArgumentNullException(nameof(turnCompletionCallbacks));

    private readonly AgentSkills _skills = skills ?? throw new ArgumentNullException(nameof(skills));

    private readonly Lock _executionGate = new();
    private readonly Lock _selectionGate = new();
    private readonly ISystemPrompt _systemPrompt = systemPrompt
        ?? throw new ArgumentNullException(nameof(systemPrompt));

    private readonly ProviderSessions _providerSessions = providerSessions;

    private AgentStatistics _statistics = eventRepository.LatestStatistics(identity.SessionId)
        ?? new AgentStatistics(0, 0, 0, 0, 0, 0, 0);

    private AgentSelection _selection = new(model, mode, security.Policy());
    private ResolvedModelSelection? _resolvedSelection;
    private bool _disposing;
    private bool _started;
    private Task<AgentExecution> _execution = Task.FromResult(AgentExecution.Succeeded(string.Empty));
    private Task<AgentExecution> _sendAndWaitTail = Task.FromResult(AgentExecution.Succeeded(string.Empty));
    private TaskCompletionSource<IncomingActivity>? _incomingInputWait;

    internal AgentSession(
        AgentIdentity identity,
        AgentSessionParentScope parentScope,
        ModelSelector model,
        ModelRouter router,
        EventBroker eventBroker,
        EventRepository eventRepository,
        IReadOnlyList<IToolFactory> toolFactories,
        ToolDefinitionCatalog toolDefinitions,
        ISystemPrompt systemPrompt,
        ToolOutputBlobStore toolOutputBlobs,
        CompactionGroupBlobStore compactionGroupBlobs,
        Compactor compactor,
        ProviderSessions providerSessions,
        ContextCadence contextCadence,
        PromptTemplateCatalog promptTemplates,
        ChildQuestionCoordinator childQuestions,
        ExitReminder exitReminder,
        IMode mode,
        IReadOnlyList<IAgentTurnCompletionCallback> turnCompletionCallbacks,
        AgentSessionSecurity security,
        RuntimeStatus status,
        AgentQueues queues,
        AgentSessionActivity activity,
        CancellationToken lifetime)
        : this(
            identity,
            parentScope,
            model,
            router,
            eventBroker,
            eventRepository,
            toolFactories,
            toolDefinitions,
            systemPrompt,
            toolOutputBlobs,
            compactionGroupBlobs,
            compactor,
            providerSessions,
            contextCadence,
            promptTemplates,
            childQuestions,
            exitReminder,
            mode,
            turnCompletionCallbacks,
            security,
            new AgentSkills(new SkillCatalog([], () => (SkillConfiguration.Default, 0L)), promptTemplates),
            status,
            queues,
            activity,
            lifetime)
    {
    }

    public string SessionId => identity.SessionId;

    public string Name => identity.Name;

    public string ParentSessionId => identity.ParentSessionId;

    public string ParentSessionName => identity.ParentSessionName;

    public int Depth => identity.Depth;

    public AgentIdentity Identity => identity;

    // Status reporting observes this session-scoped, synchronized activity log.
    public AgentSessionActivity Activity { get; } = activity
        ?? throw new ArgumentNullException(nameof(activity));

    public AgentSelection CurrentSelection()
    {
        lock (_selectionGate)
        {
            return _selection;
        }
    }

    public void UpdateSelection(ModelSelector selectedModel, IMode mode)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(mode);

        lock (_selectionGate)
        {
            if (!string.Equals(_selection.RequestedModel.Value, selectedModel.Value, StringComparison.Ordinal))
            {
                _resolvedSelection = null;
            }

            _selection = new AgentSelection(
                selectedModel,
                mode,
                mode.Profile.SecurityProfile);
        }
    }

    public void UseResolvedSelection(ResolvedModelSelection selectedModel)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        lock (_selectionGate)
        {
            _resolvedSelection = selectedModel;
        }
    }

    public void Recover() => _ = Wake(null);

    public bool Wake(IncomingActivity? activity) => WakeSelected(activity).FollowUp;

    // Stops the turn in flight and returns once the drain has unwound, so a
    // caller that sends again cannot race the turn it just stopped.
    //
    // Input admitted and not yet promoted outlives the interrupt: the drain
    // resumes for it rather than making the user ask a second time.
    public async Task Interrupt(CancellationToken cancellationToken)
    {
        Task draining;
        DrainLifecycle.DrainCancellation? stopping = null;

        lock (_drainLifecycle.Gate)
        {
            if (_drainLifecycle.Cancellation is null)
            {
                return;
            }

            draining = _drainLifecycle.Drain;

            // Somebody is already stopping this drain. Wait for the same
            // unwinding rather than cancelling and disposing it twice.
            if (!_drainLifecycle.Stopping)
            {
                _drainLifecycle.State = DrainState.Interrupting;
                Activity.ChangeState(DrainState.Interrupting);

                // Cleared behind the same gate as the capture: a wake that
                // survived it would restart the drain this is stopping.
                _drainLifecycle.Wake = false;
                _drainLifecycle.Stopping = true;
                stopping = _drainLifecycle.Cancellation;
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
            lock (_drainLifecycle.Gate)
            {
                _drainLifecycle.Stopping = false;
            }

            // The drain left it alone because the stopping flag was set, and it
            // has finished, so nothing else can be holding it.
            stopping.Release();
        }

        if (!_disposing && eventRepository.HasPendingInputs(SessionId))
        {
            _ = Wake(new IncomingActivity(IncomingActivityKind.Input, string.Empty));
        }
    }

    // Waits for the drain to finish, whatever ended it. There is no ancestor
    // Run to bound the drain by, so this is how an owner keeps its own Run from
    // returning while a turn is still writing to a database it is about to
    // close.
    public async ValueTask DisposeAsync()
    {
        lock (_drainLifecycle.Gate)
        {
            _disposing = true;
            _drainLifecycle.Wake = false;
        }

        await Interrupt(CancellationToken.None).ConfigureAwait(false);
        await _providerSessions.Close().ConfigureAwait(false);
    }

    public Task Compact(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var request = new ForcedCompactionRequest(cancellationToken);

        lock (_drainLifecycle.Gate)
        {
            if (_disposing || lifetime.IsCancellationRequested)
            {
                throw new AgentRegistryException("the user session is shutting down");
            }

            _forcedCompactions.Enqueue(request);
            if (_drainLifecycle.Cancellation is not null)
            {
                _drainLifecycle.Wake = true;
            }
            else
            {
                var cancellation = new DrainLifecycle.DrainCancellation(lifetime);
                _drainLifecycle.Cancellation = cancellation;
                _drainLifecycle.State = DrainState.Running;
                Activity.ChangeState(DrainState.Running);
                _drainLifecycle.Drain = Drain(cancellation.Token);
            }
        }

        return AwaitForcedCompaction(request);
    }

    public AgentSelection ResolvePolicySelection()
    {
        var selected = CurrentSelection();
        return selected with
        {
            SecurityProfile = identity.PolicyLineage.Resolve(selected.SecurityProfile),
        };
    }

    public AgentPolicyLineage ResolvePolicyLineage() => identity.PolicyLineage;

    public bool IsIdle() => _drainLifecycle.State == DrainState.Idle;

    public bool IsActive()
    {
        lock (_executionGate)
        {
            return _drainLifecycle.State != DrainState.Idle || (_started && !_execution.IsCompleted);
        }
    }

    public bool IsWaitingForIncomingInput()
    {
        lock (_drainLifecycle.Gate)
        {
            return _incomingInputWait is not null;
        }
    }

    public async Task<IncomingActivity?> WaitForIncomingInput(
        TimeSpan duration,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        cancellationToken.ThrowIfCancellationRequested();
        var incoming = new TaskCompletionSource<IncomingActivity>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_drainLifecycle.Gate)
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
            lock (_drainLifecycle.Gate)
            {
                if (ReferenceEquals(_incomingInputWait, incoming))
                {
                    _incomingInputWait = null;
                }
            }
        }
    }

    public async Task<(Admission Admission, bool FollowUp)> Send(
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

    public void SetExitReminder(string? reminder) => exitReminder.Set(reminder);

    public async Task<bool> ReceiveQueueNotification(
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

    public async Task<AgentSendResult> SendTextMessage(string message, CancellationToken cancellationToken)
    {
        var (result, _) = await SendAndSelectExecution(message, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<string> SendAndWaitForResult(string prompt, CancellationToken cancellationToken)
    {
        var execution = EnqueueExecution(prompt);
        var result = await execution.WaitAsync(cancellationToken).ConfigureAwait(false);

        return result.Status switch
        {
            AgentExecutionStatus.Succeeded => result.Output,
            AgentExecutionStatus.Failed =>
                throw new AgentExecutionException(AgentTaskStatus.Failed, result.Error),
            AgentExecutionStatus.Canceled =>
                throw new AgentExecutionException(AgentTaskStatus.Canceled, result.Error),
            _ => throw new InvalidOperationException("agent execution did not terminate"),
        };
    }

    public async Task ReceiveChildQuestion(
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

    public async Task ReceiveAgentCompletion(
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

    public async Task ReceiveProcessCompletion(
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

    public async Task ReceiveAgentTaskCompletion(
        string runId,
        string message,
        string messageId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runId);
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        _ = await AdmitPartsAndWake(
            [ConversationPart.TextPart(message)],
            messageId,
            Delivery.Steer,
            new IncomingActivity(IncomingActivityKind.AgentTaskCompletion, runId),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordAgentTaskCompletion(
        string message,
        string messageId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentException.ThrowIfNullOrWhiteSpace(messageId);

        var admission = eventRepository.Admit(
            SessionId,
            messageId,
            [ConversationPart.TextPart(message)],
            Delivery.Steer,
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
        if (admission.Published is not null)
        {
            await eventBroker.Publish(admission.Published, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<WaitAgentResult> Wait(
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
            return new WaitAgentResult(
                SessionId,
                Name,
                AgentTaskStatus.Running,
                Yielded: true,
                Elapsed(started),
                string.Empty,
                string.Empty);
        }
    }

    public async Task Settled() =>
        _ = await WaitForDrainResult().ConfigureAwait(false);

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

    private static List<LLMMessage> ReplaceFixedStatus(
        IReadOnlyList<LLMMessage> history,
        string content)
    {
        var replaced = history.ToList();
        var index = replaced.FindIndex(1, message => message.Role == LLMRole.System);
        if (index >= 0)
        {
            replaced[index] = LLMMessage.System(content);
        }

        return replaced;
    }

    private static string ReplaceContextStatus(string statusContent, string contextContent)
    {
        const string prefix = "Context:";
        var start = statusContent.IndexOf(prefix, StringComparison.Ordinal);
        if (start < 0)
        {
            return string.Concat(statusContent, "\n\n", contextContent);
        }

        var end = statusContent.IndexOf("\n\n", start, StringComparison.Ordinal);
        return end < 0
            ? string.Concat(statusContent.AsSpan(0, start), contextContent)
            : string.Concat(statusContent.AsSpan(0, start), contextContent, statusContent.AsSpan(end));
    }

    private static void EnsureRequestFitsAfterCompaction(ContextSnapshot context)
    {
        if (context.ExceedsInputLimit)
        {
            throw new InvalidOperationException(
                "The compacted conversation exceeds the selected model input limit.");
        }
    }

    private static LLMMessage RestoreMessage(EventRepository repository, ConversationItem item) => new()
    {
        Role = item.Role,
        Contents = repository.Materialize(item.Parts),
        ToolCalls = item.ToolCalls,
        ToolCallId = item.ToolCallId,
    };

    private static bool IsParallelSafe(ToolSnapshot snapshot, long assistantSequence, LLMToolCall call)
    {
        var tool = snapshot.Find(call.Name);
        return tool is not null
            && tool.IsParallelSafe(new ToolInvocation(call.Id, call.ArgumentsJson, assistantSequence));
    }

    private static async Task AwaitForcedCompaction(ForcedCompactionRequest request) =>
        await request.Completion.Task.WaitAsync(request.CancellationToken).ConfigureAwait(false);

    // The drain state -- the task, its cancellation, the wake flag and the
    // state itself -- is shared with whatever thread admits or interrupts, and
    // the gate is the whole of its synchronisation.
    private sealed class DrainLifecycle
    {
        internal Lock Gate { get; } = new();

        internal DrainState State { get; set; }

        internal Task<AgentExecution> Drain { get; set; } =
            Task.FromResult(AgentExecution.Succeeded(string.Empty));

        internal DrainCancellation? Cancellation { get; set; }

        internal bool Wake { get; set; }

        // Set while an interrupt is unwinding a drain. It says who disposes the
        // drain's cancellation: normally the drain does when it settles, but an
        // interrupter still holding it to cancel would then be cancelling a
        // disposed source, so it hands that duty over for the one case where the
        // two overlap.
        internal bool Stopping { get; set; }

        internal sealed class DrainCancellation(CancellationToken lifetime)
        {
            private readonly CancellationTokenSource _source =
                CancellationTokenSource.CreateLinkedTokenSource(lifetime);

            internal CancellationToken Token => _source.Token;

            internal async ValueTask CancelAsync() => await _source.CancelAsync().ConfigureAwait(false);

            internal void Release() => _source.Dispose();
        }
    }

    // The context-epoch state -- the epoch-initialised and cadence-restored
    // flags, the pending initial status, and the materialised tools -- is
    // sampled once per epoch and carried across turns.
    private sealed class EpochContext(bool initialStatusPending)
    {
        internal bool EpochInitialized { get; set; }

        internal bool ContextCadenceRestored { get; set; }

        internal bool InitialStatusPending { get; set; } = initialStatusPending;

        internal ToolSnapshot? Tools { get; set; }
    }

    private sealed class ForcedCompactionRequest(CancellationToken cancellationToken)
    {
        internal CancellationToken CancellationToken { get; } = cancellationToken;

        internal TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}

using System.Diagnostics;
using System.Text;
using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Queues;
using Parrot.Security;
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
    ModelSelector model,
    ModelRouter router,
    EventBroker eventBroker,
    EventRepository eventRepository,
    IReadOnlyList<IToolFactory> toolFactories,
    ISystemPromptProvider systemPromptProvider,
    TodoCollection todos,
    ToolOutputBlobStore toolOutputBlobs,
    Compactor compactor,
    MainAgentProfile? profile,
    SecurityProfile securityProfile,
    RuntimeStatus? status,
    AgentRegistry? registry,
    UserSession? owner,
    CancellationToken lifetime)
{
    private const string RunawayMessage = "the turn exceeded its provider-request limit";

    // What the model is told about a call the interrupt cut short. It is a tool
    // result like any other, because the provider requires one per call.
    private const string InterruptedResult = "Error: tool execution interrupted";

    private const string InterruptedFinish = "interrupted";
    private const int MaxAgentMessageBytes = 1024 * 1024;
    private const int MaxAgentResultBytes = 1024 * 1024;

    // What the conversation records where the answer would have been. Without
    // it the history ends on the prompt that was stopped, and the next drain
    // reads that as a question still owed an answer -- so interrupting a turn
    // would start it again.
    private const string InterruptedNote = "(interrupted)";

    // The conversation, carried across turns so the agent remembers. The system
    // context is sampled once per epoch and prefixed at each turn.
    private readonly List<LLMMessage> _history = status is null
        ? []
        : [.. eventRepository.ModelHistory(identity.SessionId)];

    private readonly Lock _executionGate = new();
    private readonly Lock _drainGate = new();
    private readonly Lock _selectionGate = new();
    private readonly ISystemPrompt _systemPrompt = (systemPromptProvider
        ?? throw new ArgumentNullException(nameof(systemPromptProvider))).Materialize(identity)
        ?? throw new InvalidOperationException("The system prompt provider returned no prompt.");

    private AgentStatistics _statistics = eventRepository.LatestStatistics(identity.SessionId)
        ?? new AgentStatistics(0, 0, 0, 0, 0, 0, 0);

    private string _messageId = string.Empty;

    private AgentSelection _selection = new(model, profile, securityProfile);

    private bool _epochInitialized;
    private bool _initialStatusPending = identity.Depth > 0;
    private Task<AgentExecution> _drain = Task.FromResult(AgentExecution.Succeeded(string.Empty));
    private CancellationTokenSource? _drainCancellation;
    private bool _wake;
    private bool _started;
    private Task<AgentExecution> _execution = Task.FromResult(AgentExecution.Succeeded(string.Empty));

    // Set while an interrupt is unwinding a drain. It says who disposes the
    // drain's cancellation: normally the drain does when it settles, but an
    // interrupter still holding it to cancel would then be cancelling a
    // disposed source, so it hands that duty over for the one case where the
    // two overlap.
    private bool _stopping;

    public string SessionId => identity.SessionId;

    public string Name => identity.Name;

    public string ParentSessionId => identity.ParentSessionId;

    public string ParentSessionName => identity.ParentSessionName;

    public TodoCollection Todos { get; } = todos;

    // Selection is execution state supplied by the owning user session. One
    // immutable snapshot is used for a whole turn because a running
    // drain keeps its history and pending input while later updates wait for
    // the next turn boundary.
    public string Model => Selection().RequestedModel.Value;

    // How deep this session sits below the root. The registry refuses a child
    // beyond its recursion limit.
    public int Depth => identity.Depth;

    // Read without the gate on purpose: a caller asking what a session is doing
    // gets an answer that was true when it asked, which is all any answer to
    // that question can be.
    public DrainState State { get; private set; }

    public AgentSelection Selection()
    {
        lock (_selectionGate)
        {
            return _selection;
        }
    }

    public void UpdateSelection(ModelSelector selectedModel, MainAgentProfile? profile)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);

        lock (_selectionGate)
        {
            _selection = new AgentSelection(
                selectedModel,
                profile,
                profile?.SecurityProfile ?? _selection.SecurityProfile);
        }
    }

    // Accepts a prompt. It does not run it: the prompt becomes durable here
    // (principle 1) and joins the conversation when the drain reaches the
    // boundary its delivery asks for. Waking is not waiting -- the caller is
    // told the prompt was taken, not what the model said about it.
    public async Task<Admission> Admit(
        string text, string messageId, Delivery delivery, CancellationToken cancellationToken) =>
        (await AdmitAndWake(text, messageId, delivery, cancellationToken).ConfigureAwait(false)).Admission;

    // Stops the turn in flight and returns once the drain has unwound, so a
    // caller that sends again cannot race the turn it just stopped.
    //
    // Input admitted and not yet promoted outlives the interrupt: the drain
    // resumes for it rather than making the user ask a second time.
    public async Task Interrupt(CancellationToken cancellationToken)
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
                State = DrainState.Interrupting;

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

        if (eventRepository.HasPendingInputs(SessionId))
        {
            _ = Wake();
        }
    }

    // Waits for the drain to finish, whatever ended it. There is no ancestor
    // Run to bound the drain by, so this is how an owner keeps its own Run from
    // returning while a turn is still writing to a database it is about to
    // close.
    public async Task Settled() =>
        _ = await ResultSettled().ConfigureAwait(false);

    internal bool IsIdle() => State == DrainState.Idle;

    internal Task<(Admission Admission, bool FollowUp)> Send(
        string text, string messageId, Delivery delivery, CancellationToken cancellationToken) =>
        AdmitAndWake(text, messageId, delivery, cancellationToken);

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
            _ = Wake();
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
                started = Execute(message, messageId, followUp ? cancellationToken : CancellationToken.None);
                _execution = started;
            }
        }

        if (started is null)
        {
            _ = await Send(message, messageId, Delivery.Steer, cancellationToken).ConfigureAwait(false);
        }

        return new AgentSendResult(SessionId, Name, messageId, followUp);
    }

    internal async Task ReceiveCompletion(string message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);

        var messageId = Identifier.MessageId();
        var (_, startedFollowUp) = await Send(message, messageId, Delivery.Steer, cancellationToken)
            .ConfigureAwait(false);

        if (ParentSessionId.Length == 0 || !startedFollowUp)
        {
            return;
        }

        lock (_executionGate)
        {
            _started = true;
            _execution = Execute(message, messageId, cancellationToken);
        }
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

    internal async Task<AgentExecution> ResultSettled()
    {
        Task<AgentExecution> draining;

        lock (_drainGate)
        {
            draining = _drain;
        }

        return await draining.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal Event Translate(LLMEvent llmEvent)
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
        CancellationToken cancellationToken)
    {
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
            _ = await Send(prompt, messageId, Delivery.Steer, cancellationToken).ConfigureAwait(false);
            completed = BoundResult(await ResultSettled().ConfigureAwait(false));
        }
        catch (Exception failure)
        {
            completed = AgentExecution.Failed(BoundResult(failure.Message));
        }

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

        if (registry is not null)
        {
            await registry.Deliver(identity, completed).ConfigureAwait(false);
        }

        return completed;
    }

    private async Task<(Admission Admission, bool FollowUp)> AdmitAndWake(
        string text, string messageId, Delivery delivery, CancellationToken cancellationToken)
    {
        var admission = eventRepository.Admit(
            SessionId,
            messageId,
            text,
            delivery,
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

        // Only a real admission has an event; a re-send of one already taken
        // has nothing new to publish, but still wakes, because the sender
        // re-sent precisely because they were not sure it had been.
        if (admission.Published is not null)
        {
            await eventBroker.Publish(admission.Published, cancellationToken).ConfigureAwait(false);
        }

        return (admission, Wake());
    }

    // Starts a drain, or tells the one already running that there is more to
    // take. Coalescing rather than starting a second drain is what keeps
    // principle 2: one owner, however many prompts arrive.
    private bool Wake()
    {
        lock (_drainGate)
        {
            if (_drainCancellation is not null)
            {
                _wake = true;
                return false;
            }

            // Linked to the session's lifetime, never to the request that woke
            // it: a unary call's token is cancelled when the call returns, and
            // the turn outlives the call that admitted its prompt.
            _drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            State = DrainState.Running;
            _drain = Drain(_drainCancellation.Token);
            return true;
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
                if (!cancellationToken.IsCancellationRequested
                    && (_wake || eventRepository.HasPendingInputs(SessionId)))
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
                State = DrainState.Idle;
            }

            if (completed.Status == AgentExecutionStatus.Succeeded
                && owner is not null
                && Depth == 0
                && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    _ = await owner.DeliverMonitored(this, cancellationToken).ConfigureAwait(false);
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
        ToolSnapshot? activeTools = null;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // Looking is not consuming. A pending status remains pending
                // while an idle drain has no input that could reach a provider.
                if (!Answerable() && !eventRepository.HasPendingInputs(SessionId))
                {
                    return AgentExecution.Succeeded(answer);
                }

                if (!turnOpen)
                {
                    var captured = Selection();
                    captured.Profile?.Prepare();
                    var resolved = router.Resolve(captured.RequestedModel.Value);
                    activeSelection = new AgentTurnSelection(
                        resolved.RequestedSelector,
                        resolved,
                        captured.Profile,
                        captured.SecurityProfile);
                    providerRequests = 0;
                    turnOpen = true;
                    var started = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                        TurnStarted = new TurnStarted { Model = resolved.CanonicalModel.Selector },
                    };
                    await EmitEvent(started, null, null, cancellationToken).ConfigureAwait(false);
                    activeSelection = await InjectStatus(activeSelection, cancellationToken).ConfigureAwait(false);
                    activeTools = new ToolSnapshot(
                        [.. toolFactories
                            .Where(factory => factory.Supports(this))
                            .Select(factory => factory.Create(this, activeSelection))])
                        .Without(activeSelection.Profile?.DisabledTools ?? [])
                        .Only(activeSelection.Profile?.AllowedTools);
                }

                // Status is committed before promotion, so sequenced history is
                // epoch baseline, status, then the user input it describes.
                _ = await Promote(cancellationToken).ConfigureAwait(false);

                await Epoch(activeSelection, cancellationToken).ConfigureAwait(false);

                if (activeSelection is null || activeTools is null)
                {
                    throw new AgentRegistryException("turn selection is unavailable");
                }

                var maxTurns = activeSelection.Profile?.MaxTurns ?? 24;
                if (providerRequests >= maxTurns)
                {
                    await Fail(RunawayMessage, cancellationToken).ConfigureAwait(false);
                    return AgentExecution.Failed(RunawayMessage);
                }

                var snapshot = providerRequests + 1 == maxTurns
                    ? new ToolSnapshot([])
                    : activeTools;

                var messages = new List<LLMMessage>(_history.Count + 1)
                {
                    LLMMessage.System(_systemPrompt.Build(activeSelection)),
                };
                messages.AddRange(_history);

                providerRequests++;
                var completed = await Call(activeSelection, snapshot, messages, cancellationToken)
                    .ConfigureAwait(false);

                if (completed.ToolCalls.Count > 0)
                {
                    _history.Add(LLMMessage.Assistant(completed.AssistantText, completed.ToolCalls));
                    await SettleToolCalls(snapshot, completed.ToolCalls, cancellationToken).ConfigureAwait(false);

                    if (snapshot.Tools.Count == 0)
                    {
                        await Fail(RunawayMessage, cancellationToken).ConfigureAwait(false);
                        return AgentExecution.Failed(RunawayMessage);
                    }

                    continue;
                }

                _history.Add(LLMMessage.Assistant(completed.AssistantText, []));
                _messageId = Identifier.MessageId();
                answer = completed.AssistantText;

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
                if (activeSelection.Profile?.Complete(SessionId, _messageId) is { } planCompleted)
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

                // Back to the top rather than out: a queued prompt is promoted
                // exactly here, where the turn would otherwise stop.
                turnOpen = false;
                activeSelection = null;
                activeTools = null;
            }
        }
        catch (OperationCanceledException)
        {
            // Not a failure: the turn was stopped, and it stopped with every
            // tool call settled. Reported on the same event as any other
            // ending, because it is one.
            if (turnOpen)
            {
                _history.Add(LLMMessage.Assistant(InterruptedNote, []));

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
                await EmitEvent(ended, "assistant", InterruptedNote, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return AgentExecution.Canceled();
        }
        catch (Exception failure)
        {
            // A provider or tool boundary is a deliberate containment point, and
            // this one is total: the drain is nobody's awaited task, so an
            // escaping exception would be unobserved rather than reported.
            await Fail(failure.Message, CancellationToken.None).ConfigureAwait(false);
            return AgentExecution.Failed(failure.Message);
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
            _history.Add(LLMMessage.User(promotion.Input.Content));
            await eventBroker.Publish(promotion.Published, cancellationToken).ConfigureAwait(false);
        }

        return promoted.Count;
    }

    // Whether the model owes an answer. A history ending in a user prompt or a
    // tool result is unanswered; one ending in an assistant message is not.
    private bool Answerable() =>
        _history.Count > 0 && _history[^1].Role is LLMRole.User or LLMRole.Tool;

    // Every call the model made gets a result, even when the turn is stopped
    // part-way through: a provider rejects a history holding a call with no
    // answer, so an interrupt that left one behind would break every later
    // prompt rather than only this turn (principle 6).
    private async Task SettleToolCalls(
        ToolSnapshot snapshot, IReadOnlyList<LLMToolCall> calls, CancellationToken cancellationToken)
    {
        var stopped = false;

        foreach (var call in calls)
        {
            var result = InterruptedResult;

            if (!stopped && !cancellationToken.IsCancellationRequested)
            {
                try
                {
                    result = await Invoke(snapshot, call, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    stopped = true;
                }
            }
            else
            {
                stopped = true;
                await EmitToolCancelled(call).ConfigureAwait(false);
            }

            _history.Add(LLMMessage.ToolResult(call.Id, result));
        }
    }

    // Sampled at the start of an epoch, not every turn, and compaction starts a
    // fresh one -- so a turn never begins already over the window.
    private async Task Epoch(AgentTurnSelection? selection, CancellationToken cancellationToken)
    {
        if (!_epochInitialized)
        {
            _systemPrompt.RenewEpoch();
            _epochInitialized = true;
        }

        if (!compactor.ShouldCompact(_history))
        {
            return;
        }

        // Copied before the clear: a compaction with nothing to summarise hands
        // back the very list being emptied.
        var selectedModel = selection?.ResolvedModel.CanonicalModel
            ?? throw new AgentRegistryException("turn selection is unavailable");
        var compacted = (await Compactor.Compact(
            selectedModel.Provider,
            selectedModel.ModelId,
            _history,
            cancellationToken)
            .ConfigureAwait(false)).ToList();

        _history.Clear();
        _history.AddRange(compacted);
        _systemPrompt.RenewEpoch();
    }

    private async Task<AgentTurnSelection> InjectStatus(
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        if (status is null || selection.Profile is null)
        {
            return selection;
        }

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

            if (profile is null || !string.Equals(profile.Id, pending.Mode, StringComparison.Ordinal))
            {
                selection = RefreshSelection(selection);
                selection.Profile?.Prepare();
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
                selection.Profile?.Prepare();
                continue;
            }

            _history.Add(LLMMessage.System(content));
            await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        }

        return selection;
    }

    private AgentTurnSelection RefreshSelection(AgentTurnSelection active)
    {
        var selected = Selection();
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
        IReadOnlyList<LLMMessage> messages,
        CancellationToken cancellationToken)
    {
        var selectedModel = selection?.ResolvedModel.CanonicalModel
            ?? throw new AgentRegistryException("turn selection is unavailable");
        var request = new LLMRequest
        {
            Model = selectedModel.ModelId,
            MaxTokens = 4096,
            Messages = messages,
            Tools = snapshot.Definitions,
            Reasoning = selectedModel.Reasoning,
        };

        var completed = LLMEvent.Completed(string.Empty, 0, 0, 0, string.Empty, []);

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
                _statistics = statistics;
                completed = llmEvent;
                continue;
            }

            await EmitEvent(Translate(llmEvent), null, null, cancellationToken).ConfigureAwait(false);
        }

        return completed;
    }

    private async Task<string> Invoke(
        ToolSnapshot snapshot, LLMToolCall call, CancellationToken cancellationToken)
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
            var message = $"unknown tool {call.Name}";
            await EmitToolError(call, message).ConfigureAwait(false);
            return $"error: {message}";
        }

        try
        {
            var result = await tool.Execute(call.ArgumentsJson, cancellationToken).ConfigureAwait(false);
            if (ToolOutputBlobStore.IsOversized(result))
            {
                result = await toolOutputBlobs.Persist(result, CancellationToken.None).ConfigureAwait(false);
            }

            var finished = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                ToolFinished = new ToolFinished { ToolCallId = call.Id, ToolName = call.Name, Result = result },
            };
            await EmitEvent(finished, null, null, CancellationToken.None).ConfigureAwait(false);
            return result;
        }
        catch (OperationCanceledException)
        {
            await EmitToolCancelled(call).ConfigureAwait(false);
            throw;
        }
        catch (Exception failure)
        {
            await EmitToolError(call, failure.Message).ConfigureAwait(false);
            throw;
        }
    }

    private async Task EmitToolCancelled(LLMToolCall call)
    {
        var cancelled = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            ToolCancelled = new ToolCancelled { ToolCallId = call.Id, ToolName = call.Name },
        };
        await EmitEvent(cancelled, null, null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EmitToolError(LLMToolCall call, string message)
    {
        var failed = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            ToolError = new ToolError { ToolCallId = call.Id, ToolName = call.Name, Message = message },
        };
        await EmitEvent(failed, null, null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task Fail(string message, CancellationToken cancellationToken)
    {
        var failed = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            TurnFailed = new TurnFailed { Message = message },
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
        eventRepository.Append(published, role, content);
        await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
    }
}

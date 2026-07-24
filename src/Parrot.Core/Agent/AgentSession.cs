using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
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
    ILLMProvider provider,
    EventBroker eventBroker,
    EventRepository eventRepository,
    IReadOnlyList<IToolFactory> toolFactories,
    SystemContextBuilder systemContext,
    Compactor compactor,
    ModeProfile? mode,
    RuntimeStatus? status,
    CancellationToken lifetime)
{
    // A turn that keeps calling tools without ever finishing is a runaway, not
    // work. This bounds the provider calls a promoted prompt may make; new
    // input resets it, so a long conversation is not a runaway.
    private const string RunawayMessage = "the turn exceeded its tool-call limit";

    // What the model is told about a call the interrupt cut short. It is a tool
    // result like any other, because the provider requires one per call.
    private const string InterruptedResult = "Error: tool execution interrupted";

    private const string InterruptedFinish = "interrupted";

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

    private readonly Lock _drainGate = new();
    private readonly Lock _selectionGate = new();

    private AgentSelection _selection = new(provider, string.Empty, mode);
    private string _epochContext = string.Empty;
    private Task<AgentExecution> _drain = Task.FromResult(AgentExecution.Succeeded(string.Empty));
    private CancellationTokenSource? _drainCancellation;
    private bool _wake;

    // Set while an interrupt is unwinding a drain. It says who disposes the
    // drain's cancellation: normally the drain does when it settles, but an
    // interrupter still holding it to cancel would then be cancelling a
    // disposed source, so it hands that duty over for the one case where the
    // two overlap.
    private bool _stopping;

    public string SessionId => identity.SessionId;

    public string Name => identity.Name;

    public TodoCollection Todos { get; } = new(identity.SessionId, eventRepository, eventBroker);

    // Selection is session state: an UpdateSession changes it, a prompt does
    // not. One immutable snapshot is used for a whole turn because a running
    // drain keeps its history and pending input while later updates wait for
    // the next turn boundary.
    public ILLMProvider Provider => Selection().Provider;

    public string Model
    {
        get => Selection().Model;
        init => _selection = _selection with { Model = value };
    }

    public ModeProfile? Mode => Selection().Mode;

    // How deep this session sits below the root. The registry refuses a child
    // beyond its recursion limit.
    public int Depth => identity.Depth;

    // Read without the gate on purpose: a caller asking what a session is doing
    // gets an answer that was true when it asked, which is all any answer to
    // that question can be.
    public DrainState State { get; private set; }

    // One instance per tool per session, built on first use rather than in a
    // field initializer: a tool is constructed with the session it belongs to,
    // and `this` is not available there.
    private IReadOnlyList<ITool> Tools =>
        field ??= [.. toolFactories.Select(factory => factory.Create(this))];

    public AgentSelection Selection()
    {
        lock (_selectionGate)
        {
            return _selection;
        }
    }

    public void UpdateSelection(ILLMProvider provider, string model, ModeProfile? mode)
    {
        ArgumentNullException.ThrowIfNull(provider);

        lock (_selectionGate)
        {
            _selection = new AgentSelection(provider, model, mode);
        }
    }

    // Accepts a prompt. It does not run it: the prompt becomes durable here
    // (principle 1) and joins the conversation when the drain reaches the
    // boundary its delivery asks for. Waking is not waiting -- the caller is
    // told the prompt was taken, not what the model said about it.
    public async Task<Admission> Admit(
        string text, string messageId, Delivery delivery, CancellationToken cancellationToken)
    {
        var admission = eventRepository.Admit(SessionId, messageId, text, delivery, Announce);

        // Only a real admission has an event; a re-send of one already taken
        // has nothing new to publish, but still wakes, because the sender
        // re-sent precisely because they were not sure it had been.
        if (admission.Published is not null)
        {
            await eventBroker.Publish(admission.Published, cancellationToken).ConfigureAwait(false);
        }

        Wake();

        return admission;
    }

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

    internal async Task<(Admission Admission, bool FollowUp)> Send(
        string text, string messageId, Delivery delivery, CancellationToken cancellationToken)
    {
        var admission = await Admit(text, messageId, delivery, cancellationToken).ConfigureAwait(false);
        return (admission, Wake());
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
        var published = NewEvent();

        switch (llmEvent.Kind)
        {
            case LLMEventKind.TextDelta:
                published.TextChunk = new TextChunk { Fragment = llmEvent.Text };
                break;

            case LLMEventKind.ReasoningDelta:
                published.ReasoningChunk = new ReasoningChunk { Fragment = llmEvent.Text };
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

        while (true)
        {
            var completed = await Pass(turnOpen: false, null, cancellationToken).ConfigureAwait(false);

            lock (_drainGate)
            {
                // Admitted after the last promotion looked and before the drain
                // settled. Passing again is what stops that prompt from waiting
                // for a later one to carry it.
                if (_wake && !cancellationToken.IsCancellationRequested)
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
                return completed;
            }
        }
    }

    // One pass: promote what is due, call the provider, run what it asks for,
    // and repeat until nothing is left to answer. The sequence is the one
    // docs/architecture.md fixes, and its two promotion points are the whole
    // difference between a steer and a queued prompt.
    private async Task<AgentExecution> Pass(
        bool turnOpen,
        AgentSelection? activeSelection,
        CancellationToken cancellationToken)
    {
        var answer = string.Empty;
        var rounds = 0;

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
                    activeSelection = Selection();
                    activeSelection.Mode?.Prepare();
                    turnOpen = true;
                    var started = NewEvent();
                    started.TurnStarted = new TurnStarted { Model = activeSelection.Model };
                    await EmitEvent(started, null, null, cancellationToken).ConfigureAwait(false);
                    activeSelection = await InjectStatus(activeSelection, cancellationToken).ConfigureAwait(false);
                }

                // Status is committed before promotion, so sequenced history is
                // epoch baseline, status, then the user input it describes.
                var promoted = await Promote(cancellationToken).ConfigureAwait(false);

                if (promoted > 0)
                {
                    rounds = 0;
                }

                if (rounds++ >= (activeSelection?.Mode?.MaxToolRounds ?? 24))
                {
                    await Fail(RunawayMessage, cancellationToken).ConfigureAwait(false);
                    return AgentExecution.Failed(RunawayMessage);
                }

                await Epoch(activeSelection, cancellationToken).ConfigureAwait(false);

                // One snapshot for the round, so what is offered to the model is
                // what answers it (principle 4).
                var snapshot = new ToolSnapshot(Tools);

                var messages = new List<LLMMessage>(_history.Count + 1)
                {
                    LLMMessage.System(SystemPrompt(activeSelection?.Mode)),
                };
                messages.AddRange(_history);

                var completed = await Call(activeSelection, snapshot, messages, cancellationToken)
                    .ConfigureAwait(false);

                if (completed.ToolCalls.Count > 0)
                {
                    _history.Add(LLMMessage.Assistant(completed.AssistantText, completed.ToolCalls));
                    await SettleToolCalls(snapshot, completed.ToolCalls, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _history.Add(LLMMessage.Assistant(completed.AssistantText, []));
                answer = completed.AssistantText;

                var ended = NewEvent();
                ended.TurnEnded = new TurnEnded
                {
                    FinishReason = completed.FinishReason,
                    InputTokens = completed.InputTokens,
                    OutputTokens = completed.OutputTokens,
                };
                await EmitEvent(ended, "assistant", completed.AssistantText, cancellationToken)
                    .ConfigureAwait(false);

                // Back to the top rather than out: a queued prompt is promoted
                // exactly here, where the turn would otherwise stop.
                turnOpen = false;
                activeSelection = null;
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

                var ended = NewEvent();
                ended.TurnEnded = new TurnEnded { FinishReason = InterruptedFinish };
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
        var promoted = eventRepository.PromoteSteers(SessionId, Promoted);

        if (promoted.Count == 0 && !Answerable())
        {
            promoted = eventRepository.PromoteNextQueue(SessionId, Promoted);
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
    private async Task Epoch(AgentSelection? selection, CancellationToken cancellationToken)
    {
        if (_epochContext.Length == 0)
        {
            _epochContext = systemContext.Build();
        }

        if (!compactor.ShouldCompact(_history))
        {
            return;
        }

        // Copied before the clear: a compaction with nothing to summarise hands
        // back the very list being emptied.
        var compacted = (await Compactor.Compact(
            selection?.Provider ?? Provider,
            selection?.Model ?? Model,
            _history,
            cancellationToken)
            .ConfigureAwait(false)).ToList();

        _history.Clear();
        _history.AddRange(compacted);
        _epochContext = systemContext.Build();
    }

    private string SystemPrompt(ModeProfile? activeMode) => activeMode is null
        ? _epochContext
        : $"{_epochContext}\n\n{activeMode.Prompt}\n\n{activeMode.HardRule}";

    private async Task<AgentSelection> InjectStatus(
        AgentSelection selection,
        CancellationToken cancellationToken)
    {
        if (status is null || selection.Mode is null)
        {
            return selection;
        }

        while (eventRepository.PendingStatus(SessionId) is { } pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var mode = selection.Mode;

            if (mode is null || !string.Equals(mode.Id, pending.Mode, StringComparison.Ordinal))
            {
                selection = Selection();
                selection.Mode?.Prepare();
                await Task.Yield();
                continue;
            }

            var content = await status.Observe(this, selection, mode, cancellationToken).ConfigureAwait(false);
            var published = NewEvent();
            published.StatusInjected = new StatusInjected();
            if (!eventRepository.AppendStatusPrompt(published, pending, content))
            {
                selection = Selection();
                selection.Mode?.Prepare();
                continue;
            }

            _history.Add(LLMMessage.System(content));
            await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
        }

        return selection;
    }

    // Streams one provider call: deltas go out as events, and the terminal
    // Completed is returned so the loop can decide what to do next.
    private async Task<LLMEvent> Call(
        AgentSelection? selection,
        ToolSnapshot snapshot,
        IReadOnlyList<LLMMessage> messages,
        CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = selection?.Model ?? Model,
            MaxTokens = 4096,
            Messages = messages,
            Tools = snapshot.Definitions,
        };

        var completed = LLMEvent.Completed(string.Empty, 0, 0, string.Empty, []);

        await foreach (var llmEvent in (selection?.Provider ?? Provider)
            .Call(request, cancellationToken).ConfigureAwait(false))
        {
            if (llmEvent.Kind == LLMEventKind.Completed)
            {
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
        var started = NewEvent(new ToolStarted { ToolCallId = call.Id, ToolName = call.Name });
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
            var finished = NewEvent(new ToolFinished { ToolCallId = call.Id, ToolName = call.Name });
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
        var cancelled = NewEvent(new ToolCancelled { ToolCallId = call.Id, ToolName = call.Name });
        await EmitEvent(cancelled, null, null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task EmitToolError(LLMToolCall call, string message)
    {
        var failed = NewEvent(
            new ToolError { ToolCallId = call.Id, ToolName = call.Name, Message = message });
        await EmitEvent(failed, null, null, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task Fail(string message, CancellationToken cancellationToken)
    {
        var failed = NewEvent();
        failed.TurnFailed = new TurnFailed { Message = message };
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

    // The two shapes an input takes on the stream. Composed here rather than in
    // the repository because the repository owns the transaction, not what the
    // session says about itself.
    private Event Announce(AdmittedInput input)
    {
        var published = NewEvent();

        published.InputAdmitted = new InputAdmitted
        {
            InputId = input.Id,
            MessageId = input.MessageId,
            Content = input.Content,
            Delivery = input.Delivery,
        };

        return published;
    }

    private Event Promoted(AdmittedInput input)
    {
        var published = NewEvent();

        published.InputPromoted = new InputPromoted { InputId = input.Id, MessageId = input.MessageId };

        return published;
    }

    private Event NewEvent() =>
        new() { Id = Identifier.EventId(), AgentSessionId = SessionId };

    private Event NewEvent(ToolStarted payload) =>
        new() { Id = Identifier.EventId(), AgentSessionId = SessionId, ToolStarted = payload };

    private Event NewEvent(ToolFinished payload) =>
        new() { Id = Identifier.EventId(), AgentSessionId = SessionId, ToolFinished = payload };

    private Event NewEvent(ToolCancelled payload) =>
        new() { Id = Identifier.EventId(), AgentSessionId = SessionId, ToolCancelled = payload };

    private Event NewEvent(ToolError payload) =>
        new() { Id = Identifier.EventId(), AgentSessionId = SessionId, ToolError = payload };
}

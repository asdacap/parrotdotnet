using Parrot.Context;
using Parrot.Events;
using Parrot.Llm;
using Parrot.Protocol;
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
    string sessionId,
    ILLMProvider provider,
    EventBroker eventBroker,
    EventRepository eventRepository,
    IReadOnlyList<IToolFactory> toolFactories,
    SystemContextBuilder systemContext,
    Compactor compactor,
    int depth,
    CancellationToken lifetime)
{
    // A turn that keeps calling tools without ever finishing is a runaway, not
    // work. This bounds the provider calls a promoted prompt may make; new
    // input resets it, so a long conversation is not a runaway.
    private const int MaxToolRounds = 24;

    // Recursion terminates: a subagent runs one level deeper, and a child
    // beyond this is refused rather than descended into.
    private const int MaxDepth = 4;

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
    private readonly List<LLMMessage> _history = [];

    private readonly Lock _drainGate = new();

    private string _epochContext = string.Empty;
    private Task _drain = Task.CompletedTask;
    private CancellationTokenSource? _drainCancellation;
    private bool _wake;

    // Set while an interrupt is unwinding a drain. It says who disposes the
    // drain's cancellation: normally the drain does when it settles, but an
    // interrupter still holding it to cancel would then be cancelling a
    // disposed source, so it hands that duty over for the one case where the
    // two overlap.
    private bool _stopping;

    public string SessionId { get; } = sessionId;

    // Selection is session state: an UpdateSession changes it, a prompt does
    // not. Settable rather than fixed at construction because a running drain
    // holds this session's history and its pending input, so replacing the
    // session to change a model would throw both away.
    public ILLMProvider Provider { get; set; } = provider;

    public string Model { get; set; } = string.Empty;

    // How deep this session sits below the root. A subagent runs at Depth + 1,
    // and Child refuses beyond a limit so recursion terminates.
    public int Depth { get; } = depth;

    // Read without the gate on purpose: a caller asking what a session is doing
    // gets an answer that was true when it asked, which is all any answer to
    // that question can be.
    public DrainState State { get; private set; }

    // One instance per tool per session, built on first use rather than in a
    // field initializer: a tool is constructed with the session it belongs to,
    // and `this` is not available there.
    private IReadOnlyList<ITool> Tools =>
        field ??= [.. toolFactories.Select(factory => factory.Create(this))];

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
            Wake();
        }
    }

    // Waits for the drain to finish, whatever ended it. There is no ancestor
    // Run to bound the drain by, so this is how an owner keeps its own Run from
    // returning while a turn is still writing to a database it is about to
    // close.
    public async Task Settled()
    {
        Task draining;

        lock (_drainGate)
        {
            draining = _drain;
        }

        await draining.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    // A subagent: a fresh child session sharing this one's broker and store, so
    // its events surface on the same stream. Built but not run, because the
    // spawning tool registers it on the user session first -- a subagent should
    // be visible while it runs, not only once it finishes.
    //
    // Null is the depth refusal, so recursion terminates.
    internal AgentSession? Child(int childDepth) =>
        childDepth > MaxDepth
            ? null
            : new AgentSession(
                Identifier.AgentSession(),
                Provider,
                eventBroker,
                eventRepository,
                toolFactories,
                systemContext,
                compactor,
                childDepth,
                lifetime)
            {
                Model = Model,
            };

    internal Event Translate(LLMEvent llmEvent)
    {
        var published = Compose();

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

    // A child's one subtask, run to completion and answered here rather than
    // through the drain: a subagent has no user to admit input, and the tool
    // that spawned it is waiting on the answer.
    internal async Task<string> Run(string prompt, CancellationToken cancellationToken)
    {
        var started = Compose();
        started.TurnStarted = new TurnStarted { Model = Model };

        // The prompt is durable before execution is requested (principle 1).
        await EmitEvent(started, "user", prompt, cancellationToken).ConfigureAwait(false);

        _history.Add(LLMMessage.User(prompt));

        return await Pass(turnOpen: true, cancellationToken).ConfigureAwait(false);
    }

    // Starts a drain, or tells the one already running that there is more to
    // take. Coalescing rather than starting a second drain is what keeps
    // principle 2: one owner, however many prompts arrive.
    private void Wake()
    {
        lock (_drainGate)
        {
            if (_drainCancellation is not null)
            {
                _wake = true;
                return;
            }

            // Linked to the session's lifetime, never to the request that woke
            // it: a unary call's token is cancelled when the call returns, and
            // the turn outlives the call that admitted its prompt.
            _drainCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            State = DrainState.Running;
            _drain = Drain(_drainCancellation.Token);
        }
    }

    private async Task Drain(CancellationToken cancellationToken)
    {
        // The drain belongs to the session, not to whoever admitted the prompt:
        // yielding here returns Wake to its caller instead of running the first
        // turn on the admitting thread.
        await Task.Yield();

        while (true)
        {
            _ = await Pass(turnOpen: false, cancellationToken).ConfigureAwait(false);

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
                return;
            }
        }
    }

    // One pass: promote what is due, call the provider, run what it asks for,
    // and repeat until nothing is left to answer. The sequence is the one
    // docs/architecture.md fixes, and its two promotion points are the whole
    // difference between a steer and a queued prompt.
    private async Task<string> Pass(bool turnOpen, CancellationToken cancellationToken)
    {
        var answer = string.Empty;
        var rounds = 0;

        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var promoted = await Promote(cancellationToken).ConfigureAwait(false);

                // Nothing promoted and nothing owed an answer: the pass is done.
                if (promoted == 0 && !Answerable())
                {
                    return answer;
                }

                if (promoted > 0)
                {
                    rounds = 0;
                }

                if (!turnOpen)
                {
                    turnOpen = true;
                    var started = Compose();
                    started.TurnStarted = new TurnStarted { Model = Model };
                    await EmitEvent(started, null, null, cancellationToken).ConfigureAwait(false);
                }

                if (rounds++ >= MaxToolRounds)
                {
                    await Fail(RunawayMessage, cancellationToken).ConfigureAwait(false);
                    return RunawayMessage;
                }

                await Epoch(cancellationToken).ConfigureAwait(false);

                // One snapshot for the round, so what is offered to the model is
                // what answers it (principle 4).
                var snapshot = new ToolSnapshot(Tools);

                var messages = new List<LLMMessage>(_history.Count + 1) { LLMMessage.System(_epochContext) };
                messages.AddRange(_history);

                var completed = await Call(snapshot, messages, cancellationToken).ConfigureAwait(false);

                if (completed.ToolCalls.Count > 0)
                {
                    _history.Add(LLMMessage.Assistant(completed.AssistantText, completed.ToolCalls));
                    await SettleToolCalls(snapshot, completed.ToolCalls, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                _history.Add(LLMMessage.Assistant(completed.AssistantText, []));
                answer = completed.AssistantText;

                var ended = Compose();
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

                var ended = Compose();
                ended.TurnEnded = new TurnEnded { FinishReason = InterruptedFinish };
                await EmitEvent(ended, "assistant", InterruptedNote, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            return InterruptedFinish;
        }
        catch (Exception failure)
        {
            // A provider or tool boundary is a deliberate containment point, and
            // this one is total: the drain is nobody's awaited task, so an
            // escaping exception would be unobserved rather than reported.
            await Fail(failure.Message, CancellationToken.None).ConfigureAwait(false);
            return $"error: {failure.Message}";
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
            }

            _history.Add(LLMMessage.ToolResult(call.Id, result));
        }
    }

    // Sampled at the start of an epoch, not every turn, and compaction starts a
    // fresh one -- so a turn never begins already over the window.
    private async Task Epoch(CancellationToken cancellationToken)
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
        var compacted = (await Compactor.Compact(Provider, Model, _history, cancellationToken)
            .ConfigureAwait(false)).ToList();

        _history.Clear();
        _history.AddRange(compacted);
        _epochContext = systemContext.Build();
    }

    // Streams one provider call: deltas go out as events, and the terminal
    // Completed is returned so the loop can decide what to do next.
    private async Task<LLMEvent> Call(
        ToolSnapshot snapshot, IReadOnlyList<LLMMessage> messages, CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = Model,
            MaxTokens = 4096,
            Messages = messages,
            Tools = snapshot.Definitions,
        };

        var completed = LLMEvent.Completed(string.Empty, 0, 0, string.Empty, []);

        await foreach (var llmEvent in Provider.Call(request, cancellationToken).ConfigureAwait(false))
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
        var running = Compose();
        running.ToolCallChunk = new ToolCallChunk { ToolCallId = call.Id, ToolName = call.Name };
        await EmitEvent(running, null, null, cancellationToken).ConfigureAwait(false);

        var tool = snapshot.Find(call.Name);

        return tool is null
            ? $"error: unknown tool {call.Name}"
            : await tool.Execute(call.ArgumentsJson, cancellationToken).ConfigureAwait(false);
    }

    private async Task Fail(string message, CancellationToken cancellationToken)
    {
        var failed = Compose();
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
        var published = Compose();

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
        var published = Compose();

        published.InputPromoted = new InputPromoted { InputId = input.Id, MessageId = input.MessageId };

        return published;
    }

    private Event Compose() =>
        new() { Id = Identifier.EventId(), AgentSessionId = SessionId };
}

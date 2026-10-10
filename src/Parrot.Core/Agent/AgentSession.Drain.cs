using System.Diagnostics;
using System.Text.Json;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Llm.Wire;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Security;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

internal sealed partial class AgentSession
{
    private static bool IsValidToolCallArguments(string arguments)
    {
        try
        {
            _ = JsonDocument.Parse(arguments);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private Task<string> ObserveStatusForInsertion(
        AgentTurnSelection selection,
        IAgentProfile profile,
        IReadOnlyList<LLMToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var requestHistory = HistoryWithNextPromotion();
        var instructions = _systemPrompt.Build(selection);
        return status.ObserveFitted(
            this,
            selection,
            profile,
            content => EstimateContextForHistory(selection, instructions, tools, [.. requestHistory, LLMMessage.System(content)]),
            cancellationToken);
    }

    private AgentSelection CaptureSelection()
    {
        var selected = ResolvePolicySelection();
        return selected with { SecurityProfile = security.Capture(selected.SecurityProfile) };
    }

    private SecurityProfile CaptureSecurity() => CaptureSelection().SecurityProfile;

    private AgentTurnSelection CaptureTurnSelection()
    {
        var captured = CaptureSelection();
        var resolved = ResolveModel(captured);
        return new AgentTurnSelection(resolved.RequestedSelector, resolved, captured.Profile, captured.SecurityProfile);
    }

    private ResolvedModelSelection ResolveModel(AgentSelection selection)
    {
        lock (_selectionGate)
        {
            return _resolvedSelection is { } resolved
                && resolved.RoutingSnapshot.Revision == router.RoutingRevision
                && string.Equals(resolved.RequestedSelector.Value, selection.RequestedModel.Value, StringComparison.Ordinal)
                    ? resolved
                    : router.Resolve(selection.RequestedModel.Value);
        }
    }

    private async Task<AgentExecution> WaitForDrainResult()
    {
        Task<AgentExecution> draining;

        lock (_drainLifecycle.Gate)
        {
            draining = _drainLifecycle.Drain;
        }

        return await draining.WaitAsync(CancellationToken.None).ConfigureAwait(false);
    }

    private WaitAgentResult Terminal(AgentExecution completed, long elapsedMilliseconds)
    {
        var status = completed.Status switch
        {
            AgentExecutionStatus.Succeeded => AgentTaskStatus.Succeeded,
            AgentExecutionStatus.Failed => AgentTaskStatus.Failed,
            _ => AgentTaskStatus.Canceled,
        };
        return new WaitAgentResult(SessionId, Name, status, Yielded: false, elapsedMilliseconds, completed.Output, completed.Error);
    }

    private async Task<(AgentSendResult Result, Task<AgentExecution> Execution)> SendAndSelectExecution(
        string message,
        CancellationToken cancellationToken)
    {
        EnsureMessageCanBeSent([ConversationPart.TextPart(message)]);
        var messageId = Identifier.MessageId();
        Task<AgentExecution>? execution = null;
        var followUp = false;

        lock (_executionGate)
        {
            if (_sendAndWaitTail.IsCompleted && (!_started || _execution.IsCompleted))
            {
                followUp = _started;
                _started = true;
                execution = Execute(
                    [ConversationPart.TextPart(message)],
                    messageId,
                    selectedDrain: null,
                    ownedExecution: null,
                    followUp ? cancellationToken : CancellationToken.None);
                _execution = execution;
            }
        }

        if (execution is null)
        {
            var admitted = await AdmitPartsAndWake(
                [ConversationPart.TextPart(message)],
                messageId,
                Delivery.Steer,
                new IncomingActivity(string.Empty, null),
                cancellationToken).ConfigureAwait(false);
            execution = admitted.SelectedDrain;
        }

        return (new AgentSendResult(Name, messageId, followUp), execution);
    }

    private Task<AgentExecution> EnqueueExecution(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        AgentSelection? selection,
        Action<Admission>? admitted,
        CancellationToken cancellationToken)
    {
        EnsureMessageCanBeSent(parts);
        Task<AgentExecution> execution;

        lock (_executionGate)
        {
            var predecessor = _sendAndWaitTail.IsCompleted ? _execution : _sendAndWaitTail;
            _started = true;
            execution = ExecuteAfter(predecessor, parts, messageId, selection, admitted, cancellationToken);

            // A canceled reservation completes early, but its FIFO position still waits for predecessors.
            _sendAndWaitTail = Task.WhenAll(predecessor, execution);
        }

        return execution;
    }

    private async Task<AgentExecution> ExecuteAfter(
        Task predecessor,
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        AgentSelection? selection,
        Action<Admission>? admitted,
        CancellationToken cancellationToken)
    {
        try
        {
            await predecessor.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        while (true)
        {
            Task<AgentExecution>? activeExecution = null;
            Task<AgentExecution>? execution = null;
            var steerIntoWait = false;
            lock (_executionGate)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposing || lifetime.IsCancellationRequested)
                {
                    return AgentExecution.Canceled();
                }

                if (!_started || _execution.IsCompleted)
                {
                    lock (_drainLifecycle.Gate)
                    {
                        if (_drainLifecycle.Drain.IsCompleted)
                        {
                            _started = true;
                            if (selection is not null)
                            {
                                UpdateSelection(selection);
                            }

                            execution = ExecuteOwned(parts, messageId, admitted, cancellationToken);
                            _execution = execution;
                        }
                        else
                        {
                            activeExecution = _drainLifecycle.Drain;
                        }
                    }
                }
                else
                {
                    activeExecution = _execution;
                }

                lock (_drainLifecycle.Gate)
                {
                    steerIntoWait = activeExecution is not null && _incomingInputWait is not null;
                }
            }

            if (execution is not null)
            {
                return await execution.ConfigureAwait(false);
            }

            // A turn blocked in wait is waiting for input, so queuing behind it
            // would hold this prompt until the wait times out.
            if (steerIntoWait)
            {
                var steered = await AdmitPartsAndWake(
                    parts,
                    messageId,
                    Delivery.Steer,
                    new IncomingActivity(string.Empty, null),
                    cancellationToken).ConfigureAwait(false);
                admitted?.Invoke(steered.Admission);
                return await steered.SelectedDrain.WaitAsync(cancellationToken).ConfigureAwait(false);
            }

            if (activeExecution is null)
            {
                throw new InvalidOperationException("an active execution was not selected");
            }

            try
            {
                _ = await activeExecution.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception)
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
    }

    private async Task<AgentExecution> ExecuteOwned(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        Action<Admission>? admitted,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var ownedExecution = new OwnedExecutionReservation(AdmitParts(parts, messageId, Delivery.Steer, InputSource.Unspecified), cancellationToken);
        admitted?.Invoke(ownedExecution.Admission);
        var (_, selectedDrain) = WakeSelected(
            new IncomingActivity(string.Empty, null), ownedExecution, cancellationToken);
        return await Execute(parts, messageId, selectedDrain, ownedExecution, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private void EnsureMessageCanBeSent(IReadOnlyList<ConversationPart> parts)
    {
        ArgumentNullException.ThrowIfNull(parts);
        if (lifetime.IsCancellationRequested)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        var message = string.Concat(parts.Select(part => part.Text));
        if (parts.All(part => part.Kind == ConversationPartKind.Text) && string.IsNullOrWhiteSpace(message))
        {
            throw new AgentRegistryException("no message given");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(message) > MaxAgentMessageBytes)
        {
            throw new AgentRegistryException("agent message exceeds 1048576 bytes");
        }
    }

    private async Task<AgentExecution> Execute(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        Task<AgentExecution>? selectedDrain,
        OwnedExecutionReservation? ownedExecution,
        CancellationToken cancellationToken)
    {
        var executionId = Identifier.EventId();
        var executionStarted = Stopwatch.GetTimestamp();
        diagnostics.Write(new("agent", "execution_started", DiagnosticSeverity.Information)
        {
            AgentSessionId = SessionId,
            CorrelationId = executionId,
        });
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
            await eventEmitter.Emit(started, null, null, CancellationToken.None).ConfigureAwait(false);
            if (ownedExecution is not null)
            {
                if (ownedExecution.Admission.Published is not null)
                {
                    await eventBroker.PublishWithCancellation(ownedExecution.Admission.Published, CancellationToken.None)
                        .ConfigureAwait(false);
                }

                ownedExecution.ReleaseStartup(true);
            }

            if (selectedDrain is null)
            {
                _ = await Send(
                    parts,
                    messageId,
                    Delivery.Steer,
                    new IncomingActivity(string.Empty, null),
                    cancellationToken).ConfigureAwait(false);
                completed = BoundResult(await WaitForDrainResult().ConfigureAwait(false));
            }
            else
            {
                completed = BoundResult(await selectedDrain.WaitAsync(CancellationToken.None).ConfigureAwait(false));
            }
        }
        catch (OperationCanceledException)
        {
            completed = AgentExecution.Canceled();
        }
        catch (Exception failure)
        {
            completed = AgentExecution.Failed(BoundResult(failure.Message));
        }

        if (ownedExecution is not null)
        {
            ownedExecution.ReleaseStartup(false);
            if (selectedDrain is not null)
            {
                _ = await selectedDrain.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
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
            var elapsedMilliseconds = (long)(Activity.Capture().RequestSessionDuration ?? TimeSpan.Zero).TotalMilliseconds;
            var terminal = new Event { Id = Identifier.EventId(), AgentSessionId = SessionId };
            if (completed.Status == AgentExecutionStatus.Succeeded)
            {
                terminal.AgentFinished = new AgentFinished
                {
                    ParentAgentSessionId = identity.ParentSessionId,
                    Name = Name,
                    ElapsedMs = elapsedMilliseconds,
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
                await eventEmitter.Emit(terminal, null, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                completed = AgentExecution.Failed(BoundResult(failure.Message));
            }
        }

        diagnostics.Write(new("agent", "execution_finished", completed.Status == AgentExecutionStatus.Failed ? DiagnosticSeverity.Error : DiagnosticSeverity.Information)
        {
            AgentSessionId = SessionId,
            CorrelationId = executionId,
            Outcome = completed.Status.ToString(),
            DurationMilliseconds = (long)Stopwatch.GetElapsedTime(executionStarted).TotalMilliseconds,
        });
        Activity.FinishExecution(activityExecution, completed);
        await parentScope.DeliverCompletion(identity, completed).ConfigureAwait(false);
        return completed;
    }

    private async Task<(Admission Admission, bool FollowUp, Task<AgentExecution> SelectedDrain)> AdmitPartsAndWake(
        IReadOnlyList<ConversationPart> parts,
        string messageId,
        Delivery delivery,
        IncomingActivity activity,
        CancellationToken cancellationToken)
    {
        var admission = AdmitParts(parts, messageId, delivery, activity.Source);

        // Only a real admission has an event; a re-send of one already taken
        // has nothing new to publish, but still wakes, because the sender
        // re-sent precisely because they were not sure it had been.
        if (admission.Published is not null)
        {
            await eventBroker.PublishWithCancellation(admission.Published, cancellationToken).ConfigureAwait(false);
        }

        var incoming = admission.Created || agentHistory.HasPendingInputs() ? activity : null;
        var (followUp, selectedDrain) = WakeSelected(incoming, null, CancellationToken.None);
        return (admission, followUp, selectedDrain);
    }

    private Admission AdmitParts(IReadOnlyList<ConversationPart> parts, string messageId, Delivery delivery, InputSource source) =>
        agentHistory.AdmitParts(
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
                    Source = source,
                };
                admitted.Parts.AddRange(input.Parts.Select(MessageContentPart.FromPart));
                return new Event
                {
                    Id = Identifier.EventId(),
                    AgentSessionId = SessionId,
                    InputAdmitted = admitted,
                };
            });

    private (bool FollowUp, Task<AgentExecution> SelectedDrain) WakeSelected(
        IncomingActivity? activity,
        OwnedExecutionReservation? ownedExecution,
        CancellationToken executionCancellation)
    {
        lock (_drainLifecycle.Gate)
        {
            if (_disposing)
            {
                return (false, _drainLifecycle.Drain);
            }

            if (activity is not null)
            {
                _ = _incomingInputWait?.TrySetResult(activity);
            }

            if (_drainLifecycle.Cancellation is not null)
            {
                _drainLifecycle.Wake = true;
                return (false, _drainLifecycle.Drain);
            }

            // Linked to the session's lifetime, never to the request that woke
            // it: a unary call's token is cancelled when the call returns, and
            // the turn outlives the call that admitted its prompt.
            var cancellation = new DrainLifecycle.DrainCancellation(lifetime, executionCancellation);
            _drainLifecycle.Cancellation = cancellation;
            _drainLifecycle.State = DrainState.Running;
            Activity.ChangeState(DrainState.Running);
            _drainLifecycle.Drain = Drain(ownedExecution, cancellation.Token);
            return (true, _drainLifecycle.Drain);
        }
    }

    private async Task<AgentExecution> Drain(
        OwnedExecutionReservation? ownedExecution,
        CancellationToken cancellationToken)
    {
        // The drain belongs to the session, not to whoever admitted the prompt:
        // yielding here returns Wake to its caller instead of running the first
        // turn on the admitting thread.
        await Task.Yield();

        var drainId = Identifier.EventId();
        var drainStarted = Stopwatch.GetTimestamp();
        var completed = AgentExecution.Succeeded(string.Empty);
        string? errorCode = null;
        try
        {
            diagnostics.Write(new("drain", "started", DiagnosticSeverity.Information)
            {
                AgentSessionId = SessionId,
                CorrelationId = drainId,
            });
            var started = ownedExecution is null || await ownedExecution.WaitForStartup().ConfigureAwait(false);
            while (true)
            {
                AgentExecution pass;
                try
                {
                    if (started)
                    {
                        ownedExecution?.CancellationToken.ThrowIfCancellationRequested();
                        await RunForcedCompactions(cancellationToken).ConfigureAwait(false);
                    }

                    pass = started
                        ? await Pass(turnOpen: false, null, cancellationToken).ConfigureAwait(false)
                        : AgentExecution.Canceled();
                }
                catch (OperationCanceledException) when (ownedExecution is not null)
                {
                    pass = AgentExecution.Canceled();
                }
                catch (Exception failure) when (ownedExecution is not null)
                {
                    pass = AgentExecution.Failed(BoundResult(failure.Message));
                }

                if (ownedExecution is not null
                    && (!started || ownedExecution.CancellationToken.IsCancellationRequested))
                {
                    var inputId = ownedExecution.Admission.Input.Id;
                    var canceled = agentHistory.CancelPendingInput(
                        inputId,
                        () => new Event
                        {
                            Id = Identifier.EventId(),
                            AgentSessionId = SessionId,
                            InputCanceled = new InputCanceled { InputId = inputId },
                        });
                    if (canceled is not null)
                    {
                        try
                        {
                            await eventBroker.PublishWithCancellation(canceled, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (Exception failure)
                        {
                            pass = AgentExecution.Failed(BoundResult(failure.Message));
                        }
                    }
                }

                if (pass.Status != AgentExecutionStatus.Succeeded || pass.Output.Length > 0)
                {
                    completed = pass;
                }

                lock (_drainLifecycle.Gate)
                {
                    // Admitted after the last promotion looked and before the drain
                    // settled. Check durable input as well as the in-memory wake so
                    // recovered or otherwise pre-existing input cannot be stranded.
                    if (pass.Status == AgentExecutionStatus.Succeeded
                        && !cancellationToken.IsCancellationRequested
                        && (_forcedCompactions.Count > 0 || _drainLifecycle.Wake || agentHistory.HasPendingInputs()))
                    {
                        _drainLifecycle.Wake = false;
                        continue;
                    }

                    SettleDrain(cancellationToken);
                }

                return completed;
            }
        }
        catch (Exception failure)
        {
            errorCode = DiagnosticEvent.ClassifyFailure(failure);
            lock (_drainLifecycle.Gate)
            {
                SettleDrain(cancellationToken);
            }

            throw;
        }
        finally
        {
            diagnostics.Write(new("drain", "finished", errorCode is null or "cancelled" && completed.Status != AgentExecutionStatus.Failed ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                AgentSessionId = SessionId,
                CorrelationId = drainId,
                Outcome = errorCode ?? completed.Status.ToString(),
                ErrorCode = errorCode,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(drainStarted).TotalMilliseconds,
            });
        }
    }

    private void SettleDrain(CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_drainLifecycle.Stopping, _drainLifecycle.Cancellation))
        {
            _drainLifecycle.Cancellation?.Release();
        }

        _drainLifecycle.Cancellation = null;
        while (_forcedCompactions.TryDequeue(out var forcedCompaction))
        {
            _ = forcedCompaction.Completion.TrySetCanceled(cancellationToken);
        }

        _drainLifecycle.State = DrainState.Idle;
        Activity.ChangeState(DrainState.Idle);
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
        var budget = new TurnBudget();
        var completionRetryPending = false;
        var compactedOversizedRequest = false;
        ToolSnapshot? activeTools = null;
        string? turnId = null;
        var turnStarted = Stopwatch.GetTimestamp();

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
                if (!completionRetryPending && !conversation.IsAnswerable() && !agentHistory.HasPendingInputs())
                {
                    return AgentExecution.Succeeded(answer);
                }

                if (!turnOpen)
                {
                    _skills.EndTurn();
                    activeSelection = CaptureTurnSelection();
                    var resolved = activeSelection.ResolvedModel;
                    budget.BeginTurn();
                    turnOpen = true;
                    _providerSessions.BeginTurn();
                    var aliasIcon = resolved.Alias?.Icon;
                    var started = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                        TurnStarted = new TurnStarted
                        {
                            Model = resolved.CanonicalModel.Selector,
                            ModelAliasIcon = aliasIcon is null ? null : TurnModelAliasIcon.FromAliasIcon(aliasIcon),
                        },
                    };
                    turnId = started.Id;
                    turnStarted = Stopwatch.GetTimestamp();
                    diagnostics.Write(new("turn", "started", DiagnosticSeverity.Information)
                    {
                        AgentSessionId = SessionId,
                        CorrelationId = turnId,
                    });
                    await eventEmitter.Emit(started, null, null, cancellationToken).ConfigureAwait(false);
                    EnsureEpoch();
                    activeSelection = await InjectStatus(activeSelection, cancellationToken).ConfigureAwait(false);
                    _skills.BeginTurn();
                    activeTools = PermittedTools(activeSelection);
                    await RestoreToolAvailability(cancellationToken).ConfigureAwait(false);
                }

                // Status is committed before promotion, so sequenced history is
                // epoch baseline, status, then the user input it describes.
                _ = await Promote(cancellationToken).ConfigureAwait(false);

                if (activeSelection is null || activeTools is null)
                {
                    throw new AgentRegistryException("turn selection is unavailable");
                }

                var phase = budget.Admit(activeSelection.Profile.MaxTurns, TurnInterruption.Consume());
                if (phase is null)
                {
                    await Fail(_runawayMessage, string.Empty, cancellationToken).ConfigureAwait(false);
                    FinishTurn("failed", "runaway");
                    return AgentExecution.Failed(_runawayMessage);
                }

                var snapshot = phase.FinalPhase
                    ? activeTools.EnabledAfterInterruption()
                    : activeTools;
                if (phase.EntersFinalPhase)
                {
                    await InjectFinalProviderRequestPrompt(
                        phase.Interrupted ? _finalProviderRequestAfterInterruptionPrompt : _finalProviderRequestPrompt,
                        cancellationToken).ConfigureAwait(false);
                }

                var instructions = await PrepareEpoch(
                    activeSelection,
                    snapshot.Definitions,
                    cancellationToken).ConfigureAwait(false);
                var messages = new List<LLMMessage>(conversation);
                var attempt = budget.CountRequest();
                LLMEvent completed;
                try
                {
                    completed = await providerCall.Stream(activeSelection, snapshot, instructions, messages, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (ProviderRequestTooLargeException) when (!compactedOversizedRequest)
                {
                    // Token-based triggers miss byte overflows such as inline images, so compact once and retry.
                    compactedOversizedRequest = true;
                    if (!(await CompactEpoch(activeSelection, null, snapshot.Definitions, instructions, cancellationToken)
                            .ConfigureAwait(false)).Reduced)
                    {
                        throw;
                    }

                    continue;
                }

                compactedOversizedRequest = false;

                if (completed.FinishReason == "length" && completed.ToolCalls.Count > 0)
                {
                    await RecordRetryNotice(_truncatedToolCallPrompt, attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (completed.ToolCalls.Count > 0
                    && completed.ToolCalls.Any(call => !IsValidToolCallArguments(call.ArgumentsJson)))
                {
                    await RecordRetryNotice(_invalidToolCallPrompt, attempt, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                if (completed.ToolCalls.Count > 0)
                {
                    var published = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                    };
                    agentHistory.AppendConversation(
                        published,
                        ConversationOrigin.Model,
                        LLMRole.Assistant,
                        [ConversationPart.TextPart(completed.AssistantText)],
                        completed.ToolCalls,
                        string.Empty);
                    conversation.Add(LLMMessage.Assistant(completed.AssistantText, completed.ToolCalls));
                    await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
                    Activity.RecordAssistantMessage(completed.AssistantText);
                    await eventEmitter.Emit(
                        new Event
                        {
                            Id = Identifier.EventId(),
                            AgentSessionId = SessionId,
                            ToolRequestReceived = new ToolRequestReceived
                            {
                                ToolCallCount = completed.ToolCalls.Count,
                            },
                        },
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                    await toolBatches.Reconcile(
                        activeSelection,
                        snapshot,
                        CaptureSecurity,
                        cancellationToken).ConfigureAwait(false);

                    if (phase.FinalPhase && !budget.TryExtendSettlement())
                    {
                        await Fail(_runawayMessage, string.Empty, cancellationToken).ConfigureAwait(false);
                        FinishTurn("failed", "runaway");
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
                        var systemMessage = retryOutcome.SystemMessage
                            ?? throw new InvalidOperationException("A retry outcome requires a system message.");
                        if (retryOutcome.RetainCandidateAssistant)
                        {
                            conversation.Add(LLMMessage.Assistant(completed.AssistantText, []));
                        }

                        conversation.Add(LLMMessage.System(systemMessage));
                        if (retryOutcome.RetainCandidateAssistant)
                        {
                            Activity.RecordAssistantMessage(completed.AssistantText);
                        }

                        if (retryOutcome.CompletionRetryPending is { } retryPending)
                        {
                            completionRetryPending = retryPending;
                        }

                        budget.Restart();
                        continue;
                    }

                    answer = completed.AssistantText;
                    completionRetryPending = false;
                    conversation.Add(LLMMessage.Assistant(completed.AssistantText, []));
                    var ended = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                        TurnEnded = new TurnEnded
                        {
                            FinishReason = completed.FinishReason,
                            InputTokens = CaptureStatistics().Self.Totals.InputTokens,
                            OutputTokens = CaptureStatistics().Self.Totals.OutputTokens,
                        },
                    };
                    await eventEmitter.Emit(ended, "assistant", completed.AssistantText, cancellationToken)
                        .ConfigureAwait(false);
                    FinishTurn("completed", null);
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
            FinishTurn("cancelled", null);

            // Not a failure: the turn was stopped, and it stopped with every
            // tool call settled. Reported on the same event as any other
            // ending, because it is one.
            if (turnOpen)
            {
                conversation.Add(LLMMessage.Assistant(_interruptedNote, []));

                var ended = new Event
                {
                    Id = Identifier.EventId(),
                    AgentSessionId = SessionId,
                    TurnEnded = new TurnEnded
                    {
                        FinishReason = InterruptedFinish,
                        InputTokens = CaptureStatistics().Self.Totals.InputTokens,
                        OutputTokens = CaptureStatistics().Self.Totals.OutputTokens,
                    },
                };
                await eventEmitter.Emit(ended, "assistant", _interruptedNote, CancellationToken.None)
                    .ConfigureAwait(false);
                Activity.RecordAssistantMessage(_interruptedNote);
            }

            return AgentExecution.Canceled();
        }
        catch (Exception failure)
        {
            FinishTurn("failed", ProviderRequestDiagnostics.ClassifyFailure(failure));

            // A provider or tool boundary is a deliberate containment point, and
            // this one is total: the drain is nobody's awaited task, so an
            // escaping exception would be unobserved rather than reported.
            await Fail(
                failure.Message,
                ProviderErrors.ReadResponseBody(failure),
                CancellationToken.None).ConfigureAwait(false);
            return AgentExecution.Failed(failure.Message);
        }
        finally
        {
            _skills.EndTurn();
        }

        void FinishTurn(string outcome, string? errorCode)
        {
            if (turnId is null)
            {
                return;
            }

            diagnostics.Write(new("turn", "finished", outcome == "failed" ? DiagnosticSeverity.Error : DiagnosticSeverity.Information)
            {
                AgentSessionId = SessionId,
                CorrelationId = turnId,
                Outcome = outcome,
                ErrorCode = errorCode,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(turnStarted).TotalMilliseconds,
            });
            turnId = null;
        }
    }

    // Steers first, all of them: they join the turn already running. A queued
    // prompt is taken only when nothing else is owed an answer, which is what
    // makes it a turn of its own rather than a second voice in this one.
    private async Task<int> Promote(CancellationToken cancellationToken)
    {
        var promoted = agentHistory.PromoteSteers(
            input => new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                InputPromoted = new InputPromoted { InputId = input.Id, MessageId = input.MessageId },
            });

        if (promoted.Count == 0 && !conversation.IsAnswerable())
        {
            promoted = agentHistory.PromoteNextQueue(
                input => new Event
                {
                    Id = Identifier.EventId(),
                    AgentSessionId = SessionId,
                    InputPromoted = new InputPromoted { InputId = input.Id, MessageId = input.MessageId },
                });
        }

        foreach (var promotion in promoted)
        {
            _skills.Select(promotion.Input.Parts);
            conversation.Add(LLMMessage.User(agentHistory.Materialize(promotion.Input.Parts)));
            await eventBroker.PublishWithCancellation(promotion.Published, cancellationToken).ConfigureAwait(false);
        }

        await InjectSelectedSkills(cancellationToken).ConfigureAwait(false);
        return promoted.Count;
    }

    // Recorded after the promoted inputs, matching the order the repository
    // already projected them in, so restored history equals this one.
    private async Task InjectSelectedSkills(CancellationToken cancellationToken)
    {
        var rendering = _skills.Render();
        if (rendering.Content.Length == 0)
        {
            return;
        }

        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            SkillContextInjected = new SkillContextInjected(),
        };
        _ = agentHistory.AppendMessage(published, LLMMessage.User(rendering.Content), ConversationOrigin.System);
        conversation.Add(LLMMessage.User(rendering.Content));
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
        foreach (var loadedSkillPath in rendering.LoadedPaths)
        {
            var loaded = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                SkillLoaded = new SkillLoadedEvent { Path = loadedSkillPath },
            };
            await eventEmitter.Emit(loaded, null, null, cancellationToken).ConfigureAwait(false);
        }
    }

    private ToolSnapshot MaterializeTools()
    {
        if (_epochContext.Tools is not null)
        {
            return _epochContext.Tools;
        }

        var tools = new List<ITool>(toolFactories.Count);
        var supported = new List<bool>(toolFactories.Count);
        var definitions = new List<IToolDefinition>(toolFactories.Count);
        foreach (var factory in toolFactories)
        {
            supported.Add(factory.Supports(this));
            tools.Add(factory.Create(this));
            definitions.Add(factory.Definition);
        }

        _epochContext.Tools = ToolSnapshot.Document(tools, supported, definitions);
        return _epochContext.Tools;
    }

    private async Task ReconcileToolBatchesUsingCurrentConfiguration(CancellationToken cancellationToken)
    {
        var selection = CaptureTurnSelection();
        await toolBatches.Reconcile(selection, PermittedTools(selection), CaptureSecurity, cancellationToken)
            .ConfigureAwait(false);
    }

    private ToolSnapshot PermittedTools(AgentTurnSelection selection) =>
        MaterializeTools().PermittedBy(selection.Profile);

    private void EnsureEpoch()
    {
        if (!_epochContext.EpochInitialized)
        {
            _systemPrompt.RenewEpoch();
            _epochContext.EpochInitialized = true;
        }
    }

    private async Task RecordRetryNotice(string notice, int attempt, CancellationToken cancellationToken)
    {
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
            RetryNotice = new RetryNotice
            {
                Attempt = attempt,
                Reason = notice,
            },
        };
        _ = agentHistory.AppendMessage(published, LLMMessage.System(notice), ConversationOrigin.System);
        conversation.Add(LLMMessage.System(notice));
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
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
        await eventEmitter.Emit(failed, null, null, cancellationToken).ConfigureAwait(false);
    }
}

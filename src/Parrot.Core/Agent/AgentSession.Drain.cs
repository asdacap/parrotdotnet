using Parrot.Config;
using Parrot.Context;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Questions;
using Parrot.Store;
using Parrot.Tools;

namespace Parrot.Agent;

internal sealed partial class AgentSession
{
    private async Task<string> ObserveStatusForInsertion(
        AgentTurnSelection selection,
        IAgentProfile profile,
        IReadOnlyList<LLMToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        var requestHistory = HistoryWithNextPromotion();
        var context = EstimateContextForHistory(selection, _systemPrompt.Build(selection), tools, requestHistory);
        var content = await status.ObserveWithContext(
            this,
            selection,
            profile,
            context,
            cancellationToken).ConfigureAwait(false);
        const int maximumStatusConvergenceAttempts = 8;
        for (var attempt = 0; attempt < maximumStatusConvergenceAttempts; attempt++)
        {
            var candidateHistory = new List<LLMMessage>(requestHistory) { LLMMessage.System(content) };
            var candidateContext = EstimateContextForHistory(
                selection,
                _systemPrompt.Build(selection),
                tools,
                candidateHistory);
            var contextContent = await status.ObserveContext(
                this,
                selection,
                candidateContext,
                cancellationToken).ConfigureAwait(false);
            var rendered = ReplaceContextStatus(content, contextContent);
            var renderedContext = EstimateContextForHistory(
                selection,
                _systemPrompt.Build(selection),
                tools,
                [.. requestHistory, LLMMessage.System(rendered)]);
            content = rendered;
            if (candidateContext == renderedContext)
            {
                break;
            }
        }

        return content;
    }

    private AgentSelection CaptureSelection()
    {
        var selected = ResolvePolicySelection();
        return selected with { SecurityProfile = security.Capture(selected.SecurityProfile) };
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

    private async Task<(AgentSendResult Result, Task<AgentExecution> Execution)> SendAndSelectExecution(
        string message,
        CancellationToken cancellationToken)
    {
        EnsureMessageCanBeSent(message);
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
                    message,
                    messageId,
                    selectedDrain: null,
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
                new IncomingActivity(IncomingActivityKind.Input, string.Empty),
                cancellationToken).ConfigureAwait(false);
            execution = admitted.SelectedDrain;
        }

        return (new AgentSendResult(SessionId, Name, messageId, followUp), execution);
    }

    private Task<AgentExecution> EnqueueExecution(string prompt)
    {
        EnsureMessageCanBeSent(prompt);
        var messageId = Identifier.MessageId();
        Task<AgentExecution> execution;

        lock (_executionGate)
        {
            var predecessor = _sendAndWaitTail.IsCompleted ? _execution : _sendAndWaitTail;
            _started = true;
            execution = ExecuteAfter(predecessor, prompt, messageId);
            _sendAndWaitTail = execution;
        }

        return execution;
    }

    private async Task<AgentExecution> ExecuteAfter(
        Task<AgentExecution> predecessor,
        string prompt,
        string messageId)
    {
        try
        {
            _ = await predecessor.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        while (true)
        {
            Task<AgentExecution>? activeExecution = null;
            Task<AgentExecution>? execution = null;
            lock (_executionGate)
            {
                if (_disposing || lifetime.IsCancellationRequested)
                {
                    return AgentExecution.Canceled();
                }

                if (!_started || _execution.IsCompleted)
                {
                    _started = true;
                    execution = Execute(prompt, messageId, null, CancellationToken.None);
                    _execution = execution;
                }
                else
                {
                    activeExecution = _execution;
                }
            }

            if (execution is not null)
            {
                return await execution.ConfigureAwait(false);
            }

            if (activeExecution is null)
            {
                throw new InvalidOperationException("an active execution was not selected");
            }

            try
            {
                _ = await activeExecution.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }
    }

    private void EnsureMessageCanBeSent(string message)
    {
        if (lifetime.IsCancellationRequested)
        {
            throw new AgentRegistryException("the user session is shutting down");
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new AgentRegistryException("no message given");
        }

        if (System.Text.Encoding.UTF8.GetByteCount(message) > MaxAgentMessageBytes)
        {
            throw new AgentRegistryException("agent message exceeds 1048576 bytes");
        }
    }

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
                await EmitEvent(terminal, null, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                completed = AgentExecution.Failed(BoundResult(failure.Message));
            }
        }

        Activity.FinishExecution(activityExecution, completed);
        if (parentScope.DeliveryPolicy == AgentCompletionDeliveryPolicy.Automatic
            && parentScope.Parent is { } parent)
        {
            try
            {
                if (parent.ChildRegistry.IsAccepting)
                {
                    await parent.Session.ReceiveAgentCompletion(
                        identity.Name,
                        completed.FormatCompletion(identity, parent.Session.Identity.PromptTemplates),
                        CancellationToken.None).ConfigureAwait(false);
                }
            }
            catch (Exception)
            {
            }
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

    private (bool FollowUp, Task<AgentExecution> SelectedDrain) WakeSelected(IncomingActivity? activity)
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
            var cancellation = new DrainLifecycle.DrainCancellation(lifetime);
            _drainLifecycle.Cancellation = cancellation;
            _drainLifecycle.State = DrainState.Running;
            Activity.ChangeState(DrainState.Running);
            _drainLifecycle.Drain = Drain(cancellation.Token);
            return (true, _drainLifecycle.Drain);
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

            lock (_drainLifecycle.Gate)
            {
                // Admitted after the last promotion looked and before the drain
                // settled. Check durable input as well as the in-memory wake so
                // recovered or otherwise pre-existing input cannot be stranded.
                if (pass.Status == AgentExecutionStatus.Succeeded
                    && !cancellationToken.IsCancellationRequested
                    && (_forcedCompactions.Count > 0 || _drainLifecycle.Wake || eventRepository.HasPendingInputs(SessionId)))
                {
                    _drainLifecycle.Wake = false;
                    continue;
                }

                // Left alone while an interrupt is unwinding: that caller is
                // still holding it, and disposes it once this task has ended.
                if (!_drainLifecycle.Stopping)
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
                    _skills.EndTurn();
                    var captured = CaptureSelection();
                    captured.Profile.Prepare();
                    var resolved = ResolveModel(captured);
                    activeSelection = new AgentTurnSelection(
                        resolved.RequestedSelector,
                        resolved,
                        captured.Profile,
                        captured.SecurityProfile);
                    providerRequests = 0;
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
                    if (!_epochContext.EpochInitialized)
                    {
                        _systemPrompt.RenewEpoch();
                        _epochContext.EpochInitialized = true;
                    }

                    activeSelection = await InjectStatus(activeSelection, cancellationToken).ConfigureAwait(false);
                    _skills.BeginTurn();
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
                var loadedSkillPaths = _skills.AppendTo(messages);
                foreach (var loadedSkillPath in loadedSkillPaths)
                {
                    var loaded = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                        SkillLoaded = new SkillLoadedEvent { Path = loadedSkillPath },
                    };
                    await EmitEvent(loaded, null, null, cancellationToken).ConfigureAwait(false);
                }

                providerRequests++;
                var completed = await Call(activeSelection, snapshot, instructions, messages, cancellationToken)
                    .ConfigureAwait(false);

                if (completed.FinishReason == "length" && completed.ToolCalls.Count > 0)
                {
                    var published = new Event
                    {
                        Id = Identifier.EventId(),
                        AgentSessionId = SessionId,
                        RetryNotice = new RetryNotice
                        {
                            Attempt = providerRequests,
                            Reason = _truncatedToolCallPrompt,
                        },
                    };
                    _ = eventRepository.Append(
                        published,
                        LLMMessage.System(_truncatedToolCallPrompt),
                        ConversationOrigin.System);
                    _history.Add(LLMMessage.System(_truncatedToolCallPrompt));
                    await eventBroker.Publish(published, cancellationToken).ConfigureAwait(false);
                    continue;
                }

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
                        var systemMessage = retryOutcome.SystemMessage
                            ?? throw new InvalidOperationException("A retry outcome requires a system message.");
                        if (retryOutcome.RetainCandidateAssistant)
                        {
                            _history.Add(LLMMessage.Assistant(completed.AssistantText, []));
                        }

                        _history.Add(LLMMessage.System(systemMessage));
                        if (retryOutcome.RecordAssistantActivity)
                        {
                            Activity.RecordAssistantMessage(completed.AssistantText);
                        }

                        if (retryOutcome.SelectCandidateAnswer)
                        {
                            answer = completed.AssistantText;
                        }

                        if (retryOutcome.CompletionRetryPending is { } retryPending)
                        {
                            completionRetryPending = retryPending;
                        }

                        providerRequests = 0;
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
        finally
        {
            _skills.EndTurn();
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
            _skills.Select(promotion.Input.Parts);
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
        if (_epochContext.Tools is not null)
        {
            return _epochContext.Tools;
        }

        var tools = new List<ITool>(toolFactories.Count);
        var supported = new List<bool>(toolFactories.Count);
        foreach (var factory in toolFactories)
        {
            supported.Add(factory.Supports(this));
            tools.Add(factory.Create(this));
        }

        _epochContext.Tools = ToolSnapshot.Document(tools, supported, toolDefinitions);
        return _epochContext.Tools;
    }

    private async Task ReconcileToolBatchesUsingCurrentConfiguration(CancellationToken cancellationToken)
    {
        var captured = CaptureSelection();
        var resolved = ResolveModel(captured);
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
        var maximumOutputTokens = DefaultMaximumOutputTokens;
        if (selectedModel.Model.ContextWindow > 0)
        {
            var availableOutputTokens = selectedModel.Model.ContextWindow
                - Compactor.EstimateInputTokens(instructions, snapshot.Definitions, messages);
            if (availableOutputTokens <= 0)
            {
                throw new InvalidOperationException(
                    "The conversation leaves no output capacity in the selected model context window.");
            }

            maximumOutputTokens = (int)Math.Min(DefaultMaximumOutputTokens, availableOutputTokens);
        }

        var request = new LLMRequest
        {
            Model = selectedModel.ModelId,
            MaxTokens = maximumOutputTokens,
            Instructions = instructions,
            Messages = messages,
            Tools = snapshot.Definitions,
            Reasoning = selectedModel.Reasoning,
        };

        var completed = LLMEvent.Completed(string.Empty, 0, 0, 0, string.Empty, []);

        try
        {
            Activity.BeginProviderRequest();
            await foreach (var llmEvent in _providerSessions.Get(selectedModel.Provider)
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
}

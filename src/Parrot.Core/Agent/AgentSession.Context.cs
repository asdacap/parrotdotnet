using System.Diagnostics;
using System.Globalization;
using Parrot.Config;
using Parrot.Context;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Protocol;
using Parrot.Store;

namespace Parrot.Agent;

internal sealed partial class AgentSession
{
    public ContextSnapshot EstimateContext(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);

        if (!_epochContext.EpochInitialized)
        {
            _systemPrompt.RenewEpoch();
            _epochContext.EpochInitialized = true;
        }

        var tools = MaterializeTools()
            .Without(selection.Profile.DisabledTools)
            .Only(selection.Profile.AllowedTools);
        return EstimateContextForHistory(selection, _systemPrompt.Build(selection), tools.Definitions, [.. _history]);
    }

    public IReadOnlyList<LLMToolDefinition> AdvertisedToolDefinitions(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return MaterializeTools()
            .Without(selection.Profile.DisabledTools)
            .Only(selection.Profile.AllowedTools)
            .Definitions;
    }

    public ContextSnapshot EstimateContextForTools(
        AgentTurnSelection selection,
        IReadOnlyList<LLMToolDefinition> tools)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(tools);
        return EstimateContextForHistory(selection, _systemPrompt.Build(selection), tools, [.. _history]);
    }

    public ContextSnapshot EstimateContextAfterToolResult(
        AgentTurnSelection selection,
        string toolCallId,
        string result)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentNullException.ThrowIfNull(result);

        var tools = MaterializeTools()
            .Without(selection.Profile.DisabledTools)
            .Only(selection.Profile.AllowedTools);
        var history = RestoreHistory(eventRepository, SessionId);
        var formattedResult = promptTemplates.Render(
            "tool-result.text",
            [new PromptTemplateArgument("value", result)]);
        history.Add(LLMMessage.ToolResult(toolCallId, formattedResult));
        return EstimateContextForHistory(
            selection,
            _systemPrompt.Build(selection),
            tools.Definitions,
            history);
    }

    public async Task<ContextCompactionResult> CompactFromTool(
        AgentTurnSelection selection,
        ContextSize? targetContextSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        var tools = MaterializeTools()
            .Without(selection.Profile.DisabledTools)
            .Only(selection.Profile.AllowedTools);
        if (!_epochContext.EpochInitialized)
        {
            _systemPrompt.RenewEpoch();
            _epochContext.EpochInitialized = true;
        }

        var instructions = _systemPrompt.Build(selection);
        var before = EstimateContextForHistory(selection, instructions, tools.Definitions, [.. _history]);
        if (before.InputLimit <= 0)
        {
            return new ContextCompactionResult(before, false);
        }

        var compacted = await CompactEpoch(
            selection,
            targetContextSize,
            tools.Definitions,
            instructions,
            cancellationToken).ConfigureAwait(false);
        var after = EstimateContextForHistory(
            selection,
            compacted.Instructions,
            tools.Definitions,
            [.. _history]);
        return new ContextCompactionResult(after, compacted.Reduced);
    }

    private List<LLMMessage> HistoryWithNextPromotion()
    {
        var history = new List<LLMMessage>(_history);
        history.AddRange(eventRepository.InputsForNextPromotion(SessionId)
            .Select(input => LLMMessage.User(eventRepository.Materialize(input.Parts))));
        return history;
    }

    private bool HasForcedCompactions()
    {
        lock (_drainLifecycle.Gate)
        {
            return _forcedCompactions.Count > 0;
        }
    }

    private async Task RunForcedCompactions(CancellationToken cancellationToken)
    {
        while (true)
        {
            ForcedCompactionRequest? request;
            lock (_drainLifecycle.Gate)
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
                captured.Mode.Prepare();
                var resolved = ResolveModel(captured);
                var selection = new AgentTurnSelection(
                    resolved.RequestedSelector,
                    resolved,
                    captured.Mode,
                    captured.SecurityProfile);
                var tools = MaterializeTools()
                    .Without(selection.Profile.DisabledTools)
                    .Only(selection.Profile.AllowedTools);
                if (!_epochContext.EpochInitialized)
                {
                    _systemPrompt.RenewEpoch();
                    _epochContext.EpochInitialized = true;
                }

                var instructions = _systemPrompt.Build(selection);
                _ = await CompactEpoch(selection, request.TargetContextSize, tools.Definitions, instructions, operation.Token).ConfigureAwait(false);
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
        if (!_epochContext.EpochInitialized)
        {
            _systemPrompt.RenewEpoch();
            _epochContext.EpochInitialized = true;
        }

        var instructions = _systemPrompt.Build(selection);
        var context = compactor.EstimateSelectedContext(
            selection.ResolvedModel,
            instructions,
            tools,
            _skills.HasSelection ? _skills.Augment(_history) : _history);
        var calibratedInputTokens = _providerTokenBudget.EstimateInputTokens(
            selection.ResolvedModel.CanonicalModel.Selector,
            context.EstimatedTokens);
        if (context.ExceedsTrigger
            || (context.InputLimit > 0
                && context.ExceedsCompactionTrigger(calibratedInputTokens)))
        {
            instructions = (await CompactEpoch(selection, null, tools, instructions, cancellationToken).ConfigureAwait(false)).Instructions;
            context = compactor.EstimateSelectedContext(
                selection.ResolvedModel,
                instructions,
                tools,
                _skills.HasSelection ? _skills.Augment(_history) : _history);
            EnsureRequestFitsAfterCompaction(context);
        }

        if (!_epochContext.ContextCadenceRestored)
        {
            contextCadence.Restore(eventRepository.LatestContextReminder(SessionId));
            _epochContext.ContextCadenceRestored = true;
        }

        var crossedPercentage = contextCadence.Observe(
            context,
            selection.ResolvedModel.CanonicalModel.Selector,
            _history.Count);
        if (crossedPercentage is { } percentage)
        {
            instructions = await InjectContextReminder(
                selection,
                tools,
                instructions,
                context,
                percentage,
                cancellationToken).ConfigureAwait(false);
        }

        if (_skills.HasSelection)
        {
            var requestContext = compactor.EstimateSelectedContext(
                selection.ResolvedModel,
                instructions,
                tools,
                _skills.Augment(_history));
            EnsureRequestFitsAfterCompaction(requestContext);
        }

        return instructions;
    }

    private async Task<string> InjectContextReminder(
        AgentTurnSelection selection,
        IReadOnlyList<LLMToolDefinition> tools,
        string instructions,
        ContextSnapshot context,
        int percentage,
        CancellationToken cancellationToken)
    {
        var selectedModel = selection.ResolvedModel.CanonicalModel;
        var reminder = RenderContextReminder(context, percentage);
        LLMMessage[] candidateHistory = [.. _history, LLMMessage.System(reminder)];
        var insertedContext = compactor.EstimateSelectedContext(selection.ResolvedModel, instructions, tools, candidateHistory);
        if (!insertedContext.IsAvailable)
        {
            return instructions;
        }

        if (insertedContext.ExceedsInputLimit || insertedContext.ExceedsTrigger)
        {
            var compacted = await CompactEpoch(selection, null, tools, instructions, cancellationToken).ConfigureAwait(false);
            var compactedContext = compactor.EstimateSelectedContext(selection.ResolvedModel, compacted.Instructions, tools, _history);
            EnsureRequestFitsAfterCompaction(compactedContext);
            return compacted.Instructions;
        }

        var checkpoint = new ContextReminderCheckpoint(selectedModel.Selector, context.ContextLimit, percentage);
        var published = new Event { Id = Identifier.EventId(), AgentSessionId = SessionId };
        if (!eventRepository.AppendContextReminder(published, checkpoint, context.UsagePercent.GetValueOrDefault(), reminder))
        {
            contextCadence.Acknowledge(insertedContext, selectedModel.Selector, _history.Count);
            return instructions;
        }

        _history.Add(LLMMessage.System(reminder));
        var persistedContext = compactor.EstimateSelectedContext(selection.ResolvedModel, instructions, tools, _history);
        contextCadence.Acknowledge(persistedContext, selectedModel.Selector, _history.Count);
        await eventBroker.PublishWithCancellation(published, CancellationToken.None).ConfigureAwait(false);
        return instructions;
    }

    private string RenderContextReminder(ContextSnapshot context, int percentage)
    {
        List<PromptTemplateArgument> arguments = [
            new("percentage", percentage.ToString(CultureInfo.InvariantCulture)),
            new("estimated_tokens", context.EstimatedTokens.ToString(CultureInfo.InvariantCulture)),
            new("context_limit", context.ContextLimit.ToString(CultureInfo.InvariantCulture)),
            new("notification_interval", ContextCadence.NotificationInterval.ToString(CultureInfo.InvariantCulture)),
            new("trigger", context.TriggerPercent.ToString(CultureInfo.InvariantCulture)),
        ];
        if (context.HasContextLimitOverride)
        {
            arguments.Add(new("trigger_tokens", context.TriggerTokens?.ToString(CultureInfo.InvariantCulture) ?? "unavailable"));
        }

        return promptTemplates.Render(
            context.HasContextLimitOverride ? "agent-session.context-limit-reminder" : "agent-session.context-reminder",
            arguments);
    }

    private async Task<CompactionEpochResult> CompactEpoch(
        AgentTurnSelection selection,
        ContextSize? targetContextSize,
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
        var compactionStarted = Stopwatch.GetTimestamp();
        diagnostics.Write(new("compaction", "started", DiagnosticSeverity.Information)
        {
            AgentSessionId = SessionId,
            CorrelationId = started.Id,
        });
        await EmitEvent(started, null, null, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var reduced = false;
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
                    false,
                    true));
            }

            compactionGroups.AddRange(effective.Groups.Select(group => new CompactionGroup(
                [.. group.Items.Select(item => RestoreMessage(eventRepository, item))],
                group.EndWatermark,
                group.AssistantSequence > 0 && activeCheckpoints.Contains(group.AssistantSequence),
                group.IsComplete)));
            var statusContent = await status.ObserveWithContext(
                this,
                selection,
                selection.Profile,
                EstimateContextForHistory(selection, instructions, tools, [.. _history]),
                cancellationToken).ConfigureAwait(false);
            var fixedStatus = LLMMessage.System(statusContent);
            var compacted = await compactor.CompactWithProviderSessions(
                selection.ResolvedModel,
                targetContextSize,
                instructions,
                tools,
                compactionGroups,
                effective.Snapshot?.Watermark ?? 0,
                fixedStatus,
                compactionGroupBlobs,
                _providerSessions,
                (llmEvent, token) => EmitEvent(TranslateProviderEvent(llmEvent), null, null, token),
                cancellationToken).ConfigureAwait(false);
            var currentWatermark = effective.Snapshot?.Watermark ?? 0;
            if ((compacted is null || compacted.Watermark <= currentWatermark)
                && Compactor.EstimateInputTokens(selectedModel, instructions, tools, _history) > selectedModel.Model.InputTokenLimit)
            {
                throw new InvalidOperationException("The conversation has no safe compaction boundary before the input limit.");
            }

            if (compacted is not null && compacted.Watermark > currentWatermark)
            {
                const int maximumStatusConvergenceAttempts = 8;
                var compactedHistory = ReplaceFixedStatus(compacted.History, statusContent);
                for (var attempt = 0; attempt < maximumStatusConvergenceAttempts; attempt++)
                {
                    var postCompactionContext = EstimateContextForHistory(
                        selection,
                        instructions,
                        tools,
                        compactedHistory);
                    var contextContent = await status.ObserveContext(
                        this,
                        selection,
                        postCompactionContext,
                        cancellationToken).ConfigureAwait(false);
                    var postCompactionStatus = ReplaceContextStatus(statusContent, contextContent);
                    var postCompactionHistory = ReplaceFixedStatus(compacted.History, postCompactionStatus);
                    var renderedContext = EstimateContextForHistory(
                        selection,
                        instructions,
                        tools,
                        postCompactionHistory);
                    statusContent = postCompactionStatus;
                    compactedHistory = postCompactionHistory;
                    if (postCompactionContext == renderedContext)
                    {
                        break;
                    }
                }

                compacted = compacted with { History = compactedHistory };
                var finalContext = EstimateContextForHistory(selection, instructions, tools, compacted.History);
                if (finalContext.ExceedsInputLimit)
                {
                    throw new InvalidOperationException(
                        "The compacted conversation exceeds the selected model input limit.");
                }

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

                reduced = true;
                _providerTokenBudget.Reset();
                _history.Clear();
                _history.AddRange(RestoreHistory(eventRepository, SessionId));
                var persistedContext = compactor.EstimateSelectedContext(
                    selection.ResolvedModel,
                    instructions,
                    tools,
                    _history);
                contextCadence.Rebase(
                    persistedContext,
                    selectedModel.Selector,
                    _history.Count);
                await eventBroker.PublishWithCancellation(statusInjected, CancellationToken.None).ConfigureAwait(false);
            }

            var finished = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                CompactionFinished = new CompactionFinished(),
            };
            await EmitEvent(finished, null, null, CancellationToken.None).ConfigureAwait(false);
            diagnostics.Write(new("compaction", "finished", DiagnosticSeverity.Information)
            {
                AgentSessionId = SessionId,
                CorrelationId = started.Id,
                Outcome = "completed",
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(compactionStarted).TotalMilliseconds,
            });
            return new CompactionEpochResult(instructions, reduced);
        }
        catch (Exception failure)
        {
            var errorCode = DiagnosticEvent.ClassifyFailure(failure);
            diagnostics.Write(new("compaction", "finished", errorCode == "cancelled" ? DiagnosticSeverity.Information : DiagnosticSeverity.Error)
            {
                AgentSessionId = SessionId,
                CorrelationId = started.Id,
                Outcome = errorCode == "cancelled" ? "cancelled" : "failed",
                ErrorCode = errorCode,
                DurationMilliseconds = (long)Stopwatch.GetElapsedTime(compactionStarted).TotalMilliseconds,
            });
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

    private async Task InjectFinalProviderRequestPrompt(string content, CancellationToken cancellationToken)
    {
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
        };
        eventRepository.AppendFinalProviderRequestPrompt(published, content);
        _history.Add(LLMMessage.System(content));
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
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
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AgentTurnSelection> InjectStatus(
        AgentTurnSelection selection,
        CancellationToken cancellationToken)
    {
        if (Depth > 0)
        {
            if (!_epochContext.InitialStatusPending)
            {
                return selection;
            }

            var content = await ObserveStatusForInsertion(
                selection,
                selection.Profile,
                AdvertisedToolDefinitions(selection),
                cancellationToken).ConfigureAwait(false);
            var published = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                StatusInjected = new StatusInjected(),
            };
            eventRepository.AppendInitialStatusPrompt(published, content);
            _history.Add(LLMMessage.System(content));
            _epochContext.InitialStatusPending = false;
            await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
            return selection;
        }

        while (eventRepository.PendingStatus(SessionId) is { } pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = selection.Profile;

            if (!string.Equals(profile.Id, pending.Mode, StringComparison.Ordinal))
            {
                selection = RefreshSelection(selection);
                selection.Mode.Prepare();
                await Task.Yield();
                continue;
            }

            var content = await ObserveStatusForInsertion(
                selection,
                profile,
                AdvertisedToolDefinitions(selection),
                cancellationToken).ConfigureAwait(false);
            var published = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                StatusInjected = new StatusInjected(),
            };
            if (!eventRepository.AppendStatusPrompt(published, pending, content))
            {
                selection = RefreshSelection(selection);
                selection.Mode.Prepare();
                continue;
            }

            _history.Add(LLMMessage.System(content));
            await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
        }

        return selection;
    }

    private AgentTurnSelection RefreshSelection(AgentTurnSelection active)
    {
        var selected = CaptureSelection();
        return active with
        {
            RequestedModel = selected.RequestedModel,
            ResolvedModel = ResolveModel(selected),
            Mode = selected.Mode,
            SecurityProfile = selected.SecurityProfile,
        };
    }

    private ContextSnapshot EstimateContextForHistory(
        AgentTurnSelection selection,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<LLMMessage> history)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(history);
        return compactor.EstimateSelectedContext(selection.ResolvedModel, instructions, tools, history);
    }
}

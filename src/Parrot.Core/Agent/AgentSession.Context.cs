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

        EnsureEpoch();
        var tools = PermittedTools(selection);
        return EstimateContextForHistory(selection, _systemPrompt.Build(selection), tools.Definitions, [.. conversation]);
    }

    public async Task<string> ObserveStatus(CancellationToken cancellationToken)
    {
        var selected = ResolvePolicySelection();
        var resolved = ResolveModel(selected);
        var selection = new AgentTurnSelection(resolved.RequestedSelector, resolved, selected.Profile, selected.SecurityProfile);
        var observed = await status.ObserveWithContext(this, selection, selection.Profile, EstimateContext(selection), cancellationToken)
            .ConfigureAwait(false);
        var statistics = await status.ObserveStatistics(this, selection, cancellationToken).ConfigureAwait(false);
        return string.Join("\n\n", new[] { observed, statistics }.Where(text => !string.IsNullOrWhiteSpace(text)));
    }

    public IReadOnlyList<LLMToolDefinition> AdvertisedToolDefinitions(AgentTurnSelection selection)
    {
        ArgumentNullException.ThrowIfNull(selection);
        return PermittedTools(selection).Definitions;
    }

    public ContextSnapshot EstimateContextAfterToolResult(
        AgentTurnSelection selection,
        string toolCallId,
        string result)
    {
        ArgumentNullException.ThrowIfNull(selection);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolCallId);
        ArgumentNullException.ThrowIfNull(result);

        var tools = PermittedTools(selection);
        var history = new AgentConversation(agentHistory);
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

    public async Task<ContextCompactionResult> Compact(
        AgentTurnSelection selection,
        ContextSize? targetContextSize,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selection);
        cancellationToken.ThrowIfCancellationRequested();

        var tools = PermittedTools(selection);
        EnsureEpoch();
        var instructions = _systemPrompt.Build(selection);
        var before = EstimateContextForHistory(selection, instructions, tools.Definitions, [.. conversation]);
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
            [.. conversation]);
        return new ContextCompactionResult(after, compacted.Reduced);
    }

    private List<LLMMessage> HistoryWithNextPromotion()
    {
        var history = new List<LLMMessage>(conversation);
        history.AddRange(agentHistory.InputsForNextPromotion()
            .Select(input => LLMMessage.User(agentHistory.Materialize(input.Parts))));
        return history;
    }

    // Sampled at the start of an epoch, not every turn, and compaction starts a
    // fresh one -- so a turn never begins already over the window.
    private async Task<string> PrepareEpoch(
        AgentTurnSelection selection,
        IReadOnlyList<LLMToolDefinition> tools,
        CancellationToken cancellationToken)
    {
        EnsureEpoch();
        var instructions = _systemPrompt.Build(selection);
        var context = compactor.EstimateSelectedContext(
            selection.ResolvedModel,
            instructions,
            tools,
            conversation);
        var calibratedInputTokens = providerTokenBudget.EstimateInputTokens(
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
                conversation);
            EnsureRequestFitsAfterCompaction(context);
        }

        if (!_epochContext.ContextCadenceRestored)
        {
            contextCadence.Restore(agentHistory.LatestContextReminder());
            _epochContext.ContextCadenceRestored = true;
        }

        var crossedPercentage = contextCadence.Observe(
            context,
            selection.ResolvedModel.CanonicalModel.Selector,
            conversation.Count);
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
        LLMMessage[] candidateHistory = [.. conversation, LLMMessage.System(reminder)];
        var insertedContext = compactor.EstimateSelectedContext(selection.ResolvedModel, instructions, tools, candidateHistory);
        if (!insertedContext.IsAvailable)
        {
            return instructions;
        }

        if (insertedContext.ExceedsInputLimit || insertedContext.ExceedsTrigger)
        {
            var compacted = await CompactEpoch(selection, null, tools, instructions, cancellationToken).ConfigureAwait(false);
            var compactedContext = compactor.EstimateSelectedContext(selection.ResolvedModel, compacted.Instructions, tools, conversation);
            EnsureRequestFitsAfterCompaction(compactedContext);
            return compacted.Instructions;
        }

        var checkpoint = new ContextReminderCheckpoint(selectedModel.Selector, context.ContextLimit, percentage);
        var published = new Event { Id = Identifier.EventId(), AgentSessionId = SessionId };
        if (!agentHistory.AppendContextReminder(published, checkpoint, context.UsagePercent.GetValueOrDefault(), reminder))
        {
            contextCadence.Acknowledge(insertedContext, selectedModel.Selector, conversation.Count);
            return instructions;
        }

        conversation.Add(LLMMessage.System(reminder));
        var persistedContext = compactor.EstimateSelectedContext(selection.ResolvedModel, instructions, tools, conversation);
        contextCadence.Acknowledge(persistedContext, selectedModel.Selector, conversation.Count);
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
        await eventEmitter.Emit(started, null, null, CancellationToken.None).ConfigureAwait(false);

        try
        {
            var reduced = false;
            _systemPrompt.RenewEpoch();
            instructions = _systemPrompt.Build(selection);
            var effective = agentHistory.EffectiveConversationGroups();
            var activeCheckpoints = agentHistory.ActiveCheckpointAssistantSequences();
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
                [.. group.Items.Select(item => conversation.Materialize(item))],
                group.EndWatermark,
                group.AssistantSequence > 0 && activeCheckpoints.Contains(group.AssistantSequence),
                group.IsComplete)));
            var statusContent = await status.ObserveWithContext(
                this,
                selection,
                selection.Profile,
                EstimateContextForHistory(selection, instructions, tools, [.. conversation]),
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
                providerCall.EmitRetry,
                cancellationToken).ConfigureAwait(false);
            var currentWatermark = effective.Snapshot?.Watermark ?? 0;
            if ((compacted is null || compacted.Watermark <= currentWatermark)
                && Compactor.EstimateInputTokens(selectedModel, instructions, tools, conversation) > selectedModel.Model.InputTokenLimit)
            {
                throw new InvalidOperationException("The conversation has no safe compaction boundary before the input limit.");
            }

            if (compacted is not null && compacted.Watermark > currentWatermark)
            {
                var summarized = compacted.History;
                statusContent = await status.ObserveFitted(
                    this,
                    selection,
                    selection.Profile,
                    content => EstimateContextForHistory(selection, instructions, tools, ReplaceFixedStatus(summarized, content)),
                    cancellationToken).ConfigureAwait(false);
                var compactedHistory = ReplaceFixedStatus(summarized, statusContent);
                var renderedContext = EstimateContextForHistory(selection, instructions, tools, compactedHistory);
                compacted = compacted with { History = compactedHistory };
                if (renderedContext.ExceedsInputLimit)
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
                if (!agentHistory.AppendCompactionStatus(
                        statusInjected,
                        new CompactionSnapshot(compacted.Summary.Content, compacted.Watermark),
                        statusContent))
                {
                    throw new InvalidOperationException("compaction snapshot was already persisted");
                }

                reduced = true;
                providerTokenBudget.Reset();
                conversation.Reload();
                var persistedContext = compactor.EstimateSelectedContext(
                    selection.ResolvedModel,
                    instructions,
                    tools,
                    conversation);
                contextCadence.Acknowledge(
                    persistedContext,
                    selectedModel.Selector,
                    conversation.Count);
                await eventBroker.PublishWithCancellation(statusInjected, CancellationToken.None).ConfigureAwait(false);
            }

            var finished = new Event
            {
                Id = Identifier.EventId(),
                AgentSessionId = SessionId,
                CompactionFinished = new CompactionFinished(),
            };
            await eventEmitter.Emit(finished, null, null, CancellationToken.None).ConfigureAwait(false);
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
            await eventEmitter.Emit(failed, null, null, CancellationToken.None).ConfigureAwait(false);
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
        agentHistory.AppendFinalProviderRequestPrompt(published, content);
        conversation.Add(LLMMessage.System(content));
        await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
    }

    private async Task RestoreToolAvailability(CancellationToken cancellationToken)
    {
        var published = new Event
        {
            Id = Identifier.EventId(),
            AgentSessionId = SessionId,
        };
        if (!agentHistory.AppendToolAvailabilityRestoredPrompt(published, _toolAvailabilityRestoredPrompt))
        {
            return;
        }

        conversation.Add(LLMMessage.System(_toolAvailabilityRestoredPrompt));
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
            agentHistory.AppendSystemPrompt(published, content);
            conversation.Add(LLMMessage.System(content));
            _epochContext.InitialStatusPending = false;
            await eventBroker.PublishWithCancellation(published, cancellationToken).ConfigureAwait(false);
            return selection;
        }

        while (agentHistory.PendingStatus() is { } pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var profile = selection.Profile;

            if (!string.Equals(profile.Id, pending.Mode, StringComparison.Ordinal))
            {
                selection = RefreshSelection(selection);
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
            if (!agentHistory.AppendStatusPrompt(published, pending, content))
            {
                selection = RefreshSelection(selection);
                continue;
            }

            conversation.Add(LLMMessage.System(content));
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
            Profile = selected.Profile,
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

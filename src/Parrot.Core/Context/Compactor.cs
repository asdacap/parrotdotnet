using Parrot.Config;
using Parrot.Diagnostics;
using Parrot.Llm;
using Parrot.Store;

namespace Parrot.Context;

internal sealed class Compactor(
    int triggerPercent,
    int targetPercent,
    int maximumInputTokens,
    int summaryOutputTokens,
    PromptTemplateCatalog promptTemplates)
{
    // Image token usage depends on provider-side vision processing, not encoded file size.
    private const long EstimatedImageTokens = 4096;
    private const string SummaryInstructionsTemplate = "compaction.summary-instructions";
    private const string SummaryPrefixTemplate = "compaction.summary-prefix";
    private const string OversizedToolGroupNoticeTemplate = "compaction.oversized-tool-group-notice";

    private readonly string _summaryInstructions = promptTemplates.Render(SummaryInstructionsTemplate, []);
    private readonly string _summaryPrefix = promptTemplates.Render(SummaryPrefixTemplate, []);

    public static long EstimateTokens(IReadOnlyList<LLMMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(EstimateTokens);
    }

    public static long EstimateInputTokens(
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<LLMMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(messages);

        return EstimateStringTokens(instructions)
            + tools.Sum(tool => EstimateStringTokens(tool.Name)
                + EstimateStringTokens(tool.Description)
                + EstimateStringTokens(tool.ParametersJson)
                + 12L)
            + EstimateTokens(messages)
            + 4;
    }

    public ContextSnapshot EstimateContext(
        ProviderModel selectedModel,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<LLMMessage> history)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(history);

        var estimatedTokens = EstimateInputTokens(instructions, tools, history);
        var contextLimit = selectedModel.Model.ContextWindow;
        int? usagePercent = contextLimit > 0
            ? estimatedTokens >= contextLimit
                ? 100
                : Math.Clamp((int)(estimatedTokens * 100L / contextLimit), 0, 100)
            : null;
        return new ContextSnapshot(estimatedTokens, contextLimit, usagePercent, triggerPercent)
        {
            InputLimit = selectedModel.Model.InputTokenLimit,
            TriggerTokens = selectedModel.Model.InputTokenLimit > 0
                ? (long)selectedModel.Model.InputTokenLimit * triggerPercent / 100
                : null,
        };
    }

    public ContextCompactionPolicy ResolvePolicy(ResolvedModelSelection selectedModel, ContextSize? targetContextSize)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        return ContextCompactionPolicy.Resolve(
            selectedModel.ContextLimit,
            targetContextSize,
            selectedModel.CanonicalModel.Model.ContextWindow,
            selectedModel.CanonicalModel.Model.InputTokenLimit,
            triggerPercent,
            targetPercent);
    }

    public ContextSnapshot EstimateSelectedContext(
        ResolvedModelSelection selectedModel,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<LLMMessage> history)
    {
        var policy = ResolvePolicy(selectedModel, null);
        var snapshot = EstimateContext(selectedModel.CanonicalModel, instructions, tools, history);
        var effectiveTriggerPercent = selectedModel.ContextLimit is not null
            && snapshot.ContextLimit > 0 && policy.TriggerTokens is { } triggerTokens
            ? (int)Math.Clamp(triggerTokens >= snapshot.ContextLimit ? 100 : triggerTokens * 100 / snapshot.ContextLimit, 0, 100)
            : triggerPercent;
        return snapshot with
        {
            TriggerTokens = policy.TriggerTokens,
            TriggerPercent = effectiveTriggerPercent,
            HasContextLimitOverride = selectedModel.ContextLimit is not null,
        };
    }

    public bool ShouldCompact(
        ProviderModel selectedModel,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<LLMMessage> history) =>
        EstimateContext(selectedModel, instructions, tools, history).ExceedsTrigger;

    public async Task<CompactionResult?> Compact(
        ProviderModel selectedModel,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<LLMMessage> history,
        LLMMessage fixedMessage,
        CompactionGroupBlobStore groupBlobs,
        IDiagnosticLog diagnostics,
        string agentSessionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(fixedMessage);
        ArgumentNullException.ThrowIfNull(groupBlobs);

        var groups = Groups(history)
            .Select((messages, index) => new CompactionGroup(
                messages,
                index + 1L,
                false,
                IsComplete(messages)))
            .ToList();
        var targetBudget = selectedModel.Model.InputTokenLimit > 0
            ? PercentageBudget(selectedModel.Model.InputTokenLimit, targetPercent)
            : (long?)null;
        var providerSessions = new ProviderSessions(diagnostics, agentSessionId);
        try
        {
            return await CompactCore(
                selectedModel,
                targetBudget,
                instructions,
                tools,
                groups,
                0,
                fixedMessage,
                groupBlobs,
                providerSessions,
                static (_, _) => ValueTask.CompletedTask,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await providerSessions.Close().ConfigureAwait(false);
        }
    }

    public async Task<CompactionResult?> Compact(
        ProviderModel selectedModel,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<CompactionGroup> groups,
        long baseWatermark,
        LLMMessage fixedMessage,
        CompactionGroupBlobStore groupBlobs,
        IDiagnosticLog diagnostics,
        string agentSessionId,
        CancellationToken cancellationToken)
    {
        var targetBudget = selectedModel.Model.InputTokenLimit > 0
            ? PercentageBudget(selectedModel.Model.InputTokenLimit, targetPercent)
            : (long?)null;
        var providerSessions = new ProviderSessions(diagnostics, agentSessionId);
        try
        {
            return await CompactCore(
                selectedModel,
                targetBudget,
                instructions,
                tools,
                groups,
                baseWatermark,
                fixedMessage,
                groupBlobs,
                providerSessions,
                static (_, _) => ValueTask.CompletedTask,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await providerSessions.Close().ConfigureAwait(false);
        }
    }

    internal Task<CompactionResult?> CompactWithProviderSessions(
        ResolvedModelSelection selectedModel,
        ContextSize? targetContextSize,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<CompactionGroup> groups,
        long baseWatermark,
        LLMMessage fixedMessage,
        CompactionGroupBlobStore groupBlobs,
        ProviderSessions providerSessions,
        Func<LLMEvent, CancellationToken, ValueTask> emitRetry,
        CancellationToken cancellationToken) =>
        CompactCore(
            selectedModel.CanonicalModel,
            ResolvePolicy(selectedModel, targetContextSize).TargetTokens,
            instructions,
            tools,
            groups,
            baseWatermark,
            fixedMessage,
            groupBlobs,
            providerSessions,
            emitRetry,
            cancellationToken);

    private static bool IsComplete(IReadOnlyList<LLMMessage> messages)
    {
        var toolCalls = messages.SelectMany(message => message.ToolCalls).ToArray();
        return toolCalls.Length == 0 || toolCalls.All(call => messages.Any(message =>
            message.Role == LLMRole.Tool
            && string.Equals(message.ToolCallId, call.Id, StringComparison.Ordinal)));
    }

    private static bool IsEligibleForSpill(CompactionGroup group) =>
        group.IsComplete
        && group.Messages.Count > 0
        && group.Messages[0].Role == LLMRole.Assistant
        && group.Messages[0].ToolCalls.Count > 0;

    private static long EstimateTokens(LLMMessage message) =>
        message.Contents.Sum(content => content.Kind switch
        {
            LLMContentKind.Text => EstimateStringTokens(content.Text),
            LLMContentKind.Image => EstimatedImageTokens,
            _ => throw new InvalidOperationException($"unsupported LLM content kind {content.Kind}"),
        })
        + message.ToolCalls.Sum(call => EstimateStringTokens(call.Id) + EstimateStringTokens(call.Name)
            + EstimateStringTokens(call.ArgumentsJson))
        + EstimateStringTokens(message.ToolCallId)
        + 8;

    private static IEnumerable<IReadOnlyList<LLMMessage>> Groups(IReadOnlyList<LLMMessage> messages)
    {
        for (var index = 0; index < messages.Count; index++)
        {
            var group = new List<LLMMessage> { messages[index] };
            if (messages[index].Role == LLMRole.Assistant && messages[index].ToolCalls.Any())
            {
                while (index + 1 < messages.Count && messages[index + 1].Role == LLMRole.Tool)
                {
                    group.Add(messages[++index]);
                }
            }

            yield return group;
        }
    }

    private static List<LLMMessage> MessagesOf(IEnumerable<IReadOnlyList<LLMMessage>> groups) =>
        [.. groups.SelectMany(group => group)];

    private static string BoundSummary(string summary, int maximumOutputTokens)
    {
        if (EstimateStringTokens(summary) <= maximumOutputTokens)
        {
            return summary;
        }

        var maximumCharacters = checked(maximumOutputTokens * 4);
        return summary[..Math.Min(summary.Length, maximumCharacters)];
    }

    private static long EstimateStringTokens(string value) => (value.Length + 3L) / 4L;

    private static long PercentageBudget(int contextWindow, int percentage)
    {
        if (contextWindow <= 0)
        {
            throw new InvalidOperationException("The selected model must provide a positive context window.");
        }

        return ((long)contextWindow * percentage) / 100;
    }

    private async Task<CompactionResult?> CompactCore(
        ProviderModel selectedModel,
        long? resolvedTargetBudget,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<CompactionGroup> groups,
        long baseWatermark,
        LLMMessage fixedMessage,
        CompactionGroupBlobStore groupBlobs,
        ProviderSessions providerSessions,
        Func<LLMEvent, CancellationToken, ValueTask> emitRetry,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(fixedMessage);
        ArgumentNullException.ThrowIfNull(groupBlobs);
        ArgumentNullException.ThrowIfNull(providerSessions);
        if (groups.Count < 2 || resolvedTargetBudget is not { } targetBudget)
        {
            return null;
        }

        var inputTokenLimit = selectedModel.Model.InputTokenLimit;
        var keepGroupFrom = groups.Count - 1;
        var retained = groups[^1].Messages.ToList();

        while (keepGroupFrom > 1)
        {
            var candidate = groups[keepGroupFrom - 1].Messages.Concat(retained).ToList();
            if (EstimateWithSummary(instructions, tools, fixedMessage, candidate) + 1 > targetBudget)
            {
                break;
            }

            retained = candidate;
            keepGroupFrom--;
        }

        var naturalRequiredExceedsTarget = EstimateWithSummary(instructions, tools, fixedMessage, retained) + 1
            > targetBudget;
        var checkpointCut = Enumerable.Range(0, groups.Count)
            .Where(index => groups[index].HasCheckpointBefore)
            .Select(index => new
            {
                Index = index,
                Retained = groups.Skip(index).SelectMany(group => group.Messages).ToList(),
            })
            .Where(candidate =>
            {
                var estimate = EstimateWithSummary(instructions, tools, fixedMessage, candidate.Retained) + 1;
                return estimate <= targetBudget || (naturalRequiredExceedsTarget && estimate <= inputTokenLimit);
            })
            .OrderBy(candidate => Math.Abs(candidate.Index - keepGroupFrom))
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
        if (checkpointCut is not null)
        {
            keepGroupFrom = checkpointCut.Index;
            retained = checkpointCut.Retained;
        }

        var incompleteGroup = groups.Take(keepGroupFrom).Select((group, index) => new { Group = group, Index = index })
            .FirstOrDefault(candidate => !candidate.Group.IsComplete);
        if (incompleteGroup is not null)
        {
            keepGroupFrom = incompleteGroup.Index;
            retained = [.. groups.Skip(keepGroupFrom).SelectMany(group => group.Messages)];
        }

        if (keepGroupFrom == 0 || groups[keepGroupFrom - 1].EndWatermark <= baseWatermark)
        {
            return null;
        }

        var summaryBaseTokens = EstimateWithSummary(instructions, tools, fixedMessage, retained);
        var targetExceededByRequiredContext = summaryBaseTokens + 1 > targetBudget;
        var summaryBudget = (targetExceededByRequiredContext ? inputTokenLimit : targetBudget) - summaryBaseTokens;
        if (summaryBudget <= 0)
        {
            throw new InvalidOperationException("The recent conversation leaves no room for a compaction summary.");
        }

        var summaryTokens = checked((int)Math.Min(summaryOutputTokens, summaryBudget));
        var inputBudget = Math.Min((long)maximumInputTokens, inputTokenLimit);
        if (selectedModel.Model.ContextWindow > 0)
        {
            inputBudget = Math.Min(inputBudget, selectedModel.Model.ContextWindow - summaryTokens);
        }

        if (inputBudget <= 0)
        {
            throw new InvalidOperationException("The selected model leaves no room for a compaction request.");
        }

        var toSummarise = groups.Take(keepGroupFrom).ToList();
        var substitutions = new Dictionary<CompactionGroup, LLMMessage>(ReferenceEqualityComparer.Instance);
        var providerGroups = new Dictionary<CompactionGroup, IReadOnlyList<LLMMessage>>(
            ReferenceEqualityComparer.Instance);
        foreach (var group in toSummarise)
        {
            var providerGroup = group.Messages;
            if (EstimateRequestTokens(string.Empty, [], providerGroup) > inputBudget)
            {
                if (!IsEligibleForSpill(group))
                {
                    throw new InvalidOperationException("A complete conversation group exceeds the compaction input budget.");
                }

                var path = await groupBlobs.Persist(group, cancellationToken).ConfigureAwait(false);
                var notice = LLMMessage.System(promptTemplates.Render(
                    OversizedToolGroupNoticeTemplate,
                    [new PromptTemplateArgument("path", path)]));
                substitutions.Add(group, notice);
                providerGroup = [notice];
                if (EstimateRequestTokens(string.Empty, [], providerGroup) > inputBudget)
                {
                    throw new InvalidOperationException("A complete conversation group exceeds the compaction input budget.");
                }
            }

            providerGroups.Add(group, providerGroup);
        }

        var summary = string.Empty;
        var chunk = new List<IReadOnlyList<LLMMessage>>();
        foreach (var group in toSummarise)
        {
            var providerGroup = providerGroups[group];
            if (chunk.Count > 0
                && EstimateRequestTokens(summary, MessagesOf(chunk), providerGroup) > inputBudget)
            {
                summary = await FoldGroups(
                    selectedModel,
                    summary,
                    chunk,
                    summaryTokens,
                    providerSessions,
                    emitRetry,
                    cancellationToken);
                chunk = [];
            }

            if (EstimateRequestTokens(summary, MessagesOf(chunk), providerGroup) > inputBudget)
            {
                if (!IsEligibleForSpill(group) || substitutions.ContainsKey(group))
                {
                    throw new InvalidOperationException("A complete conversation group exceeds the compaction input budget.");
                }

                var path = await groupBlobs.Persist(group, cancellationToken).ConfigureAwait(false);
                var notice = LLMMessage.System(promptTemplates.Render(
                    OversizedToolGroupNoticeTemplate,
                    [new PromptTemplateArgument("path", path)]));
                substitutions.Add(group, notice);
                providerGroup = [notice];
                if (EstimateRequestTokens(summary, MessagesOf(chunk), providerGroup) > inputBudget)
                {
                    throw new InvalidOperationException("A complete conversation group exceeds the compaction input budget.");
                }
            }

            chunk.Add(providerGroup);
        }

        if (chunk.Count > 0)
        {
            summary = await FoldGroups(
                selectedModel,
                summary,
                chunk,
                summaryTokens,
                providerSessions,
                emitRetry,
                cancellationToken);
        }

        var summaryMessage = LLMMessage.System($"{_summaryPrefix}{summary}");
        IReadOnlyList<LLMMessage> compacted = [summaryMessage, fixedMessage, .. retained];
        var compactedTokens = EstimateInputTokens(instructions, tools, compacted);
        if (compactedTokens > inputTokenLimit)
        {
            throw new InvalidOperationException("The compacted conversation exceeds the selected model input limit.");
        }

        if (!targetExceededByRequiredContext && compactedTokens > targetBudget)
        {
            throw new InvalidOperationException("The compacted conversation exceeds the configured target.");
        }

        var watermark = keepGroupFrom == 0 ? baseWatermark : groups[keepGroupFrom - 1].EndWatermark;
        return new CompactionResult(compacted, summaryMessage, retained.Count, watermark);
    }

    private long EstimateWithSummary(
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        LLMMessage fixedMessage,
        IReadOnlyList<LLMMessage> retained) =>
        EstimateInputTokens(instructions, tools, [LLMMessage.System(_summaryPrefix), fixedMessage, .. retained]);

    private long EstimateRequestTokens(
        string precedingSummary,
        IReadOnlyList<LLMMessage> chunk,
        IReadOnlyList<LLMMessage> nextGroup)
    {
        var messages = RequestMessages(precedingSummary, chunk, nextGroup);
        return EstimateTokens(messages);
    }

    private List<LLMMessage> RequestMessages(
        string precedingSummary,
        IReadOnlyList<LLMMessage> chunk,
        IReadOnlyList<LLMMessage> nextGroup)
    {
        var messages = new List<LLMMessage>
        {
            LLMMessage.System(_summaryInstructions),
        };
        if (precedingSummary.Length > 0)
        {
            messages.Add(LLMMessage.System($"{_summaryPrefix}{precedingSummary}"));
        }

        messages.AddRange(chunk);
        messages.AddRange(nextGroup);
        return messages;
    }

    // A summarisation that comes back empty (a reasoning model may spend its
    // output budget on reasoning and emit nothing visible) is retried with a
    // smaller group: the pending groups split in half and each half folded, the
    // second carrying the first's summary forward. A full fold clears the chunk;
    // a partial one strictly halves it, so it terminates without dropping a group.
    private async Task<string> FoldGroups(
        ProviderModel selectedModel,
        string precedingSummary,
        List<IReadOnlyList<LLMMessage>> pending,
        int maximumOutputTokens,
        ProviderSessions providerSessions,
        Func<LLMEvent, CancellationToken, ValueTask> emitRetry,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 1)
        {
            var single = await Summarise(
                selectedModel,
                precedingSummary,
                pending[0],
                maximumOutputTokens,
                providerSessions,
                emitRetry,
                cancellationToken).ConfigureAwait(false);
            return single ?? throw new InvalidOperationException(
                "The compaction provider did not complete with a summary.");
        }

        var whole = await Summarise(
            selectedModel,
            precedingSummary,
            MessagesOf(pending),
            maximumOutputTokens,
            providerSessions,
            emitRetry,
            cancellationToken).ConfigureAwait(false);
        if (whole is not null)
        {
            return whole;
        }

        var half = (pending.Count + 1) / 2;
        var firstHalf = await FoldGroups(
            selectedModel,
            precedingSummary,
            [.. pending.Take(half)],
            maximumOutputTokens,
            providerSessions,
            emitRetry,
            cancellationToken).ConfigureAwait(false);
        return await FoldGroups(
            selectedModel,
            firstHalf,
            [.. pending.Skip(half)],
            maximumOutputTokens,
            providerSessions,
            emitRetry,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<string?> Summarise(
        ProviderModel selectedModel,
        string precedingSummary,
        IReadOnlyList<LLMMessage> chunk,
        int maximumOutputTokens,
        ProviderSessions providerSessions,
        Func<LLMEvent, CancellationToken, ValueTask> emitRetry,
        CancellationToken cancellationToken)
    {
        var request = new LLMRequest
        {
            Model = selectedModel.ModelId,
            MaxTokens = maximumOutputTokens,
            Messages = RequestMessages(precedingSummary, chunk, []),
            Reasoning = selectedModel.Reasoning,
        };

        string? summary = null;
        await foreach (var llmEvent in providerSessions.Get(selectedModel.Provider)
            .Call(request, cancellationToken).ConfigureAwait(false))
        {
            if (summary is not null)
            {
                throw new InvalidOperationException("The compaction provider emitted an event after completion.");
            }

            if (llmEvent.Kind == LLMEventKind.Retry)
            {
                await emitRetry(llmEvent, cancellationToken).ConfigureAwait(false);
            }
            else if (llmEvent.Kind == LLMEventKind.Completed)
            {
                summary = string.IsNullOrEmpty(llmEvent.AssistantText) ? null : llmEvent.AssistantText;
            }
        }

        return summary is null ? null : BoundSummary(summary, maximumOutputTokens);
    }
}

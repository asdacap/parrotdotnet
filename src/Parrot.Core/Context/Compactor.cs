using Parrot.Llm;

namespace Parrot.Context;

internal sealed class Compactor(
    int triggerPercent,
    int targetPercent,
    int maximumInputTokens,
    int summaryOutputTokens)
{
    private const string SummaryInstructions = "Summarise the following conversation so it can continue. Keep decisions, "
        + "file paths, open tasks, and relevant evidence from images. Be terse.";

    private const string SummaryPrefix = "Summary of the earlier conversation:\n";

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

    public bool ShouldCompact(
        ProviderModel selectedModel,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<LLMMessage> history)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        var contextWindow = selectedModel.Model.ContextWindow;
        return contextWindow > 0
            && EstimateInputTokens(instructions, tools, history)
                > PercentageBudget(contextWindow, triggerPercent);
    }

    public async Task<CompactionResult?> Compact(
        ProviderModel selectedModel,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<LLMMessage> history,
        LLMMessage fixedMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(history);
        ArgumentNullException.ThrowIfNull(fixedMessage);

        var groups = Groups(history)
            .Select((messages, index) => new CompactionGroup(messages, index + 1L, false))
            .ToList();
        return await Compact(
            selectedModel,
            instructions,
            tools,
            groups,
            0,
            fixedMessage,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<CompactionResult?> Compact(
        ProviderModel selectedModel,
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        IReadOnlyList<CompactionGroup> groups,
        long baseWatermark,
        LLMMessage fixedMessage,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(instructions);
        ArgumentNullException.ThrowIfNull(tools);
        ArgumentNullException.ThrowIfNull(groups);
        ArgumentNullException.ThrowIfNull(fixedMessage);
        if (groups.Count < 2)
        {
            return null;
        }

        var contextWindow = selectedModel.Model.ContextWindow;
        var targetBudget = PercentageBudget(contextWindow, targetPercent);
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
                return estimate <= targetBudget || (naturalRequiredExceedsTarget && estimate <= contextWindow);
            })
            .OrderBy(candidate => Math.Abs(candidate.Index - keepGroupFrom))
            .ThenBy(candidate => candidate.Index)
            .FirstOrDefault();
        if (checkpointCut is not null)
        {
            keepGroupFrom = checkpointCut.Index;
            retained = checkpointCut.Retained;
        }

        var summaryBaseTokens = EstimateWithSummary(instructions, tools, fixedMessage, retained);
        var targetExceededByRequiredContext = summaryBaseTokens + 1 > targetBudget;
        var summaryBudget = (targetExceededByRequiredContext ? contextWindow : targetBudget) - summaryBaseTokens;
        if (summaryBudget <= 0)
        {
            throw new InvalidOperationException("The recent conversation leaves no room for a compaction summary.");
        }

        var summaryTokens = checked((int)Math.Min(summaryOutputTokens, summaryBudget));
        var inputBudget = Math.Min((long)maximumInputTokens, contextWindow - summaryTokens);
        if (inputBudget <= 0)
        {
            throw new InvalidOperationException("The selected model leaves no room for a compaction request.");
        }

        var toSummarise = groups.Take(keepGroupFrom).Select(group => group.Messages).ToList();
        foreach (var group in toSummarise)
        {
            if (EstimateRequestTokens(string.Empty, group) > inputBudget)
            {
                throw new InvalidOperationException("A complete conversation group exceeds the compaction input budget.");
            }
        }

        var summary = string.Empty;
        var chunk = new List<LLMMessage>();
        foreach (var group in toSummarise)
        {
            if (chunk.Count > 0 && EstimateRequestTokens(summary, chunk, group) > inputBudget)
            {
                summary = await Summarise(
                    selectedModel,
                    summary,
                    chunk,
                    summaryTokens,
                    cancellationToken).ConfigureAwait(false);
                chunk.Clear();
            }

            if (EstimateRequestTokens(summary, chunk, group) > inputBudget)
            {
                throw new InvalidOperationException("A complete conversation group exceeds the compaction input budget.");
            }

            chunk.AddRange(group);
        }

        summary = await Summarise(
            selectedModel,
            summary,
            chunk,
            summaryTokens,
            cancellationToken).ConfigureAwait(false);
        var summaryMessage = LLMMessage.System($"{SummaryPrefix}{summary}");
        IReadOnlyList<LLMMessage> compacted = [summaryMessage, fixedMessage, .. retained];
        var compactedTokens = EstimateInputTokens(instructions, tools, compacted);
        if (compactedTokens > contextWindow)
        {
            throw new InvalidOperationException("The compacted conversation exceeds the selected model context window.");
        }

        if (!targetExceededByRequiredContext && compactedTokens > targetBudget)
        {
            throw new InvalidOperationException("The compacted conversation exceeds the configured target.");
        }

        var watermark = keepGroupFrom == 0 ? baseWatermark : groups[keepGroupFrom - 1].EndWatermark;
        return new CompactionResult(compacted, summaryMessage, retained.Count, watermark);
    }

    private static long EstimateWithSummary(
        string instructions,
        IReadOnlyList<LLMToolDefinition> tools,
        LLMMessage fixedMessage,
        IReadOnlyList<LLMMessage> retained) =>
        EstimateInputTokens(instructions, tools, [LLMMessage.System(SummaryPrefix), fixedMessage, .. retained]);

    private static long EstimateTokens(LLMMessage message) =>
        message.Contents.Sum(content => content.Kind switch
        {
            LLMContentKind.Text => EstimateStringTokens(content.Text),
            LLMContentKind.Image => EstimateImageTokens(content.Image.Length),
            _ => throw new InvalidOperationException($"unsupported LLM content kind {content.Kind}"),
        })
        + message.ToolCalls.Sum(call => EstimateStringTokens(call.Id) + EstimateStringTokens(call.Name)
            + EstimateStringTokens(call.ArgumentsJson))
        + EstimateStringTokens(message.ToolCallId)
        + 8;

    private static long EstimateRequestTokens(
        string precedingSummary,
        IReadOnlyList<LLMMessage> chunk,
        IReadOnlyList<LLMMessage> nextGroup)
    {
        var messages = RequestMessages(precedingSummary, chunk, nextGroup);
        return EstimateTokens(messages);
    }

    private static long EstimateRequestTokens(string precedingSummary, IReadOnlyList<LLMMessage> chunk) =>
        EstimateTokens(RequestMessages(precedingSummary, chunk, []));

    private static List<LLMMessage> RequestMessages(
        string precedingSummary,
        IReadOnlyList<LLMMessage> chunk,
        IReadOnlyList<LLMMessage> nextGroup)
    {
        var messages = new List<LLMMessage>
        {
            LLMMessage.System(SummaryInstructions),
        };
        if (precedingSummary.Length > 0)
        {
            messages.Add(LLMMessage.System($"{SummaryPrefix}{precedingSummary}"));
        }

        messages.AddRange(chunk);
        messages.AddRange(nextGroup);
        return messages;
    }

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

    private static long EstimateStringTokens(string value) => (value.Length + 3L) / 4L;

    private static long EstimateImageTokens(int byteLength) =>
        Math.Max(1024L, (byteLength + 2L) / 3L);

    private static long PercentageBudget(int contextWindow, int percentage)
    {
        if (contextWindow <= 0)
        {
            throw new InvalidOperationException("The selected model must provide a positive context window.");
        }

        return ((long)contextWindow * percentage) / 100;
    }

    private static async Task<string> Summarise(
        ProviderModel selectedModel,
        string precedingSummary,
        IReadOnlyList<LLMMessage> chunk,
        int maximumOutputTokens,
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
        await foreach (var llmEvent in selectedModel.Provider.Call(request, cancellationToken).ConfigureAwait(false))
        {
            if (summary is not null)
            {
                throw new InvalidOperationException("The compaction provider emitted an event after completion.");
            }

            if (llmEvent.Kind == LLMEventKind.Completed)
            {
                summary = llmEvent.AssistantText;
            }
        }

        if (string.IsNullOrEmpty(summary))
        {
            throw new InvalidOperationException("The compaction provider did not complete with a summary.");
        }

        return BoundSummary(summary, maximumOutputTokens);
    }

    private static string BoundSummary(string summary, int maximumOutputTokens)
    {
        if (EstimateStringTokens(summary) <= maximumOutputTokens)
        {
            return summary;
        }

        var maximumCharacters = checked(maximumOutputTokens * 4);
        return summary[..Math.Min(summary.Length, maximumCharacters)];
    }
}

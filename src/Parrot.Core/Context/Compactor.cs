using Parrot.Llm;

namespace Parrot.Context;

internal sealed class Compactor(int tokenBudget)
{
    private const int MaximumCompactionInputTokens = 60_000;
    private const int SummaryOutputTokens = 1024;
    private const string SummaryInstructions = "Summarise the following conversation so it can continue. Keep decisions, "
        + "file paths, open tasks, and relevant evidence from images. Be terse.";

    public static int EstimateTokens(IReadOnlyList<LLMMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(EstimateTokens);
    }

    public async Task<IReadOnlyList<LLMMessage>> Compact(
        ProviderModel selectedModel,
        IReadOnlyList<LLMMessage> history,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(selectedModel);
        ArgumentNullException.ThrowIfNull(history);

        var keepFrom = history.Count - Math.Min(4, history.Count);
        while (keepFrom > 0 && history[keepFrom].Role == LLMRole.Tool)
        {
            keepFrom--;
        }

        var toSummarise = history.Take(keepFrom).ToList();
        if (toSummarise.Count == 0)
        {
            return history;
        }

        var inputBudget = InputBudget(selectedModel.Model.ContextWindow);
        var summary = string.Empty;
        foreach (var group in Groups(toSummarise))
        {
            if (EstimateTokens(group) > inputBudget)
            {
                throw new InvalidOperationException("A complete conversation group exceeds the compaction input budget.");
            }
        }

        var chunk = new List<LLMMessage>();
        foreach (var group in Groups(toSummarise))
        {
            if (chunk.Count > 0 && EstimateRequestTokens(summary, chunk, group) > inputBudget)
            {
                summary = await Summarise(selectedModel, summary, chunk, cancellationToken).ConfigureAwait(false);
                chunk.Clear();
            }

            if (EstimateRequestTokens(summary, chunk, group) > inputBudget)
            {
                throw new InvalidOperationException("A complete conversation group exceeds the compaction input budget.");
            }

            chunk.AddRange(group);
        }

        summary = await Summarise(selectedModel, summary, chunk, cancellationToken).ConfigureAwait(false);
        var compacted = new List<LLMMessage>
        {
            LLMMessage.System($"Summary of the earlier conversation:\n{summary}"),
        };
        compacted.AddRange(history.Skip(keepFrom));
        if (EstimateTokens(compacted) > inputBudget)
        {
            throw new InvalidOperationException("The recent conversation exceeds the compaction input budget.");
        }

        return compacted;
    }

    public bool ShouldCompact(IReadOnlyList<LLMMessage> history) =>
        EstimateTokens(history) > tokenBudget;

    private static int EstimateTokens(LLMMessage message) =>
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

    private static int EstimateRequestTokens(
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
            messages.Add(LLMMessage.System($"Summary of the earlier conversation:\n{precedingSummary}"));
        }

        messages.AddRange(chunk);
        messages.AddRange(nextGroup);
        return EstimateTokens(messages);
    }

    private static async Task<string> Summarise(
        ProviderModel selectedModel,
        string precedingSummary,
        IReadOnlyList<LLMMessage> chunk,
        CancellationToken cancellationToken)
    {
        var messages = new List<LLMMessage>
        {
            LLMMessage.System(SummaryInstructions),
        };
        if (precedingSummary.Length > 0)
        {
            messages.Add(LLMMessage.System($"Summary of the earlier conversation:\n{precedingSummary}"));
        }

        messages.AddRange(chunk);
        var request = new LLMRequest
        {
            Model = selectedModel.ModelId,
            MaxTokens = SummaryOutputTokens,
            Messages = messages,
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

        return summary;
    }

    private static IEnumerable<IReadOnlyList<LLMMessage>> Groups(List<LLMMessage> messages)
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

    private static int EstimateStringTokens(string value) => (value.Length + 3) / 4;

    private static int EstimateImageTokens(int byteLength) =>
        Math.Max(1024, checked((byteLength + 2) / 3));

    private int InputBudget(int contextWindow)
    {
        var budget = tokenBudget > 0
            ? Math.Min(MaximumCompactionInputTokens, tokenBudget)
            : MaximumCompactionInputTokens;
        return contextWindow > 0 ? Math.Min(budget, contextWindow / 4) : budget;
    }
}

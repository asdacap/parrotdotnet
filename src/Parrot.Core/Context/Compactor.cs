using Parrot.Llm;

namespace Parrot.Context;

// Keeps a conversation inside the model's context window. When the running
// history grows past a token budget, the older half is summarised by the
// provider and replaced with that summary -- completing a compaction and
// starting a new epoch, as the architecture requires.
internal sealed class Compactor(ILLMProvider provider, int tokenBudget)
{
    // Rough, deliberately: one token is about four characters. Exact counting
    // needs the model's tokenizer, which this does not carry; the budget has
    // margin for the error.
    public static int EstimateTokens(IReadOnlyList<LLMMessage> messages)
    {
        ArgumentNullException.ThrowIfNull(messages);
        return messages.Sum(message => (message.Content.Length / 4) + 8);
    }

    public bool ShouldCompact(IReadOnlyList<LLMMessage> history) =>
        EstimateTokens(history) > tokenBudget;

    // Summarises everything but the last few messages and returns a fresh, short
    // history: the summary followed by what was kept. The tail is kept verbatim
    // so the model does not lose the thread of what it was just doing.
    public async Task<IReadOnlyList<LLMMessage>> Compact(
        string model, IReadOnlyList<LLMMessage> history, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(history);

        var keep = Math.Min(4, history.Count);
        var toSummarise = history.Take(history.Count - keep).ToList();

        if (toSummarise.Count == 0)
        {
            return history;
        }

        var transcript = string.Join(
            "\n", toSummarise.Select(message => $"{message.Role}: {message.Content}"));

        var request = new LLMRequest
        {
            Model = model,
            MaxTokens = 1024,
            Messages =
            [
                LLMMessage.System("Summarise this conversation so it can continue. Keep decisions, "
                    + "file paths, and open tasks. Be terse."),
                LLMMessage.User(transcript),
            ],
        };

        var summary = new System.Text.StringBuilder();

        await foreach (var llmEvent in provider.Call(request, cancellationToken).ConfigureAwait(false))
        {
            if (llmEvent.Kind == LLMEventKind.Completed)
            {
                _ = summary.Append(llmEvent.AssistantText);
            }
        }

        return
        [
            LLMMessage.System($"Summary of the earlier conversation:\n{summary}"),
            .. history.Skip(history.Count - keep),
        ];
    }
}

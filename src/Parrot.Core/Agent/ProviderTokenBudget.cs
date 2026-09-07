using Parrot.Llm;

namespace Parrot.Agent;

internal sealed class ProviderTokenBudget
{
    private const int DefaultMaximumOutputTokens = 32 * 1024;

    private string? _selector;
    private long _estimatedInputTokens;
    private int _actualInputTokens;

    public long EstimateInputTokens(string selector, long estimatedInputTokens)
    {
        estimatedInputTokens = Math.Max(0, estimatedInputTokens);
        if (!string.Equals(selector, _selector, StringComparison.Ordinal))
        {
            return estimatedInputTokens;
        }

        var calibration = Math.Max(0, _actualInputTokens - _estimatedInputTokens);
        return Math.Min(estimatedInputTokens, long.MaxValue - calibration) + calibration;
    }

    public void ObserveUsage(string selector, long estimatedInputTokens, LLMEvent completed)
    {
        ArgumentNullException.ThrowIfNull(completed);
        if (completed.InputTokens <= 0)
        {
            return;
        }

        _selector = selector;
        _estimatedInputTokens = Math.Max(0, estimatedInputTokens);
        _actualInputTokens = completed.InputTokens;
    }

    public void Reset()
    {
        _selector = null;
        _estimatedInputTokens = 0;
        _actualInputTokens = 0;
    }

    public int CalculateMaximumOutputTokens(string selector, long estimatedInputTokens, LLMModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var calibratedInputTokens = EstimateInputTokens(selector, estimatedInputTokens);
        if (model.MaxInputTokens > 0 && calibratedInputTokens > model.InputTokenLimit)
        {
            throw new InvalidOperationException("The conversation exceeds the selected model input limit.");
        }

        var maximumOutputTokens = model.MaxOutputTokens > 0
            ? Math.Min(DefaultMaximumOutputTokens, model.MaxOutputTokens)
            : DefaultMaximumOutputTokens;
        if (model.ContextWindow > 0)
        {
            var headroom = (calibratedInputTokens / 20) + (calibratedInputTokens % 20 > 0 ? 1 : 0);
            var availableOutputTokens = model.ContextWindow - calibratedInputTokens;
            if (availableOutputTokens <= headroom)
            {
                throw new InvalidOperationException(
                    "The conversation leaves no output capacity in the selected model context window.");
            }

            maximumOutputTokens = (int)Math.Min(maximumOutputTokens, availableOutputTokens - headroom);
        }

        return maximumOutputTokens;
    }
}

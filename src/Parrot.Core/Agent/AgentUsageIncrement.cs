using Parrot.Llm;

namespace Parrot.Agent;

internal sealed record AgentUsageIncrement(
    AgentUsageKey Key,
    AgentUsageTotals Totals,
    long? ContextSize,
    long? ContextLimit)
{
    public static AgentUsageIncrement FromCompletion(
        ProviderModel model,
        ReasoningOptions? reasoning,
        LLMEvent completed)
    {
        var statistics = new AgentStatistics(0, 0, 0, 0, 0, 0, 0).Add(completed, model.Model);
        return new(
            AgentUsageKey.FromRequest(model, reasoning),
            new(
                statistics.InputTokens,
                statistics.CachedInputTokens,
                statistics.OutputTokens,
                0,
                statistics.InputCost,
                statistics.OutputCost),
            statistics.ContextSize,
            statistics.ContextLimit);
    }

    public static AgentUsageIncrement FromToolStart(AgentUsageKey key) =>
        new(key, new(0, 0, 0, 1, 0, 0), null, null);
}

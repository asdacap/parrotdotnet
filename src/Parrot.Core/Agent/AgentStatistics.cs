using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Agent;

internal sealed record AgentStatistics(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ContextSize,
    long ContextLimit,
    double InputCost,
    double OutputCost)
{
    public AgentStatistics Add(LLMEvent completed, LLMModel model)
    {
        var inputTokens = Math.Max(0, completed.InputTokens);
        var cachedInputTokens = Math.Clamp(completed.CachedInputTokens, 0, inputTokens);
        var cachedInputPrice = model.Fields.HasFlag(ModelMetadataFields.CachedInputPrice)
            ? model.CachedInputPrice
            : model.InputPrice;
        var inputCost = ((inputTokens - cachedInputTokens) * model.InputPrice)
            + (cachedInputTokens * cachedInputPrice);

        return new(
            checked(InputTokens + completed.InputTokens),
            checked(CachedInputTokens + completed.CachedInputTokens),
            checked(OutputTokens + completed.OutputTokens),
            completed.InputTokens,
            model.ContextWindow,
            InputCost + inputCost,
            OutputCost + (completed.OutputTokens * model.OutputPrice));
    }

    public AgentStatisticsUpdatedEvent ConvertToPayload() =>
        new()
        {
            InputTokens = InputTokens,
            CachedInputTokens = CachedInputTokens,
            OutputTokens = OutputTokens,
            ContextSize = ContextSize,
            ContextLimit = ContextLimit,
            InputCost = InputCost,
            OutputCost = OutputCost,
        };

    public static AgentStatistics Restore(AgentStatisticsUpdatedEvent payload) =>
        new(
            payload.InputTokens,
            payload.CachedInputTokens,
            payload.OutputTokens,
            payload.ContextSize,
            payload.ContextLimit,
            payload.InputCost,
            payload.OutputCost);
}

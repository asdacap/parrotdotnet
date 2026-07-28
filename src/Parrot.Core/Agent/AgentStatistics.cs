using Parrot.Llm;
using Parrot.Protocol;

namespace Parrot.Agent;

internal sealed record AgentStatistics(
    long InputTokens,
    long CachedInputTokens,
    long OutputTokens,
    long ContextSize,
    long ContextLimit)
{
    public AgentStatistics Add(LLMEvent completed, long contextLimit) =>
        new(
            checked(InputTokens + completed.InputTokens),
            checked(CachedInputTokens + completed.CachedInputTokens),
            checked(OutputTokens + completed.OutputTokens),
            completed.InputTokens,
            contextLimit);

    public AgentStatisticsUpdatedEvent ConvertToPayload() =>
        new()
        {
            InputTokens = InputTokens,
            CachedInputTokens = CachedInputTokens,
            OutputTokens = OutputTokens,
            ContextSize = ContextSize,
            ContextLimit = ContextLimit,
        };

    public static AgentStatistics Restore(AgentStatisticsUpdatedEvent payload) =>
        new(
            payload.InputTokens,
            payload.CachedInputTokens,
            payload.OutputTokens,
            payload.ContextSize,
            payload.ContextLimit);
}

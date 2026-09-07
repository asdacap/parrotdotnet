using Parrot.Agent;

namespace Parrot.Protocol;

public sealed partial class AgentStatisticsUpdatedEvent
{
    internal static AgentStatisticsUpdatedEvent From(AgentStatistics statistics) =>
        new()
        {
            InputTokens = statistics.InputTokens,
            CachedInputTokens = statistics.CachedInputTokens,
            OutputTokens = statistics.OutputTokens,
            ContextSize = statistics.ContextSize,
            ContextLimit = statistics.ContextLimit,
            InputCost = statistics.InputCost,
            OutputCost = statistics.OutputCost,
        };
}

namespace Parrot.Agent;

internal sealed record AgentSessionStatisticsSnapshot(
    AgentUsageSnapshot Self,
    AgentUsageSnapshot Cumulative,
    long ContextSize,
    long ContextLimit)
{
    public static AgentSessionStatisticsSnapshot Empty { get; } = new(
        AgentUsageSnapshot.Empty, AgentUsageSnapshot.Empty, 0, 0);

    public bool HasIncompleteLegacyToolCounts { get; init; }

    public AgentStatistics ToSelfStatistics() => new(
        Self.Totals.InputTokens,
        Self.Totals.CachedInputTokens,
        Self.Totals.OutputTokens,
        ContextSize,
        ContextLimit,
        Self.Totals.InputCost,
        Self.Totals.OutputCost);
}
